using System.Globalization;
using FinXmlProcessor.Application.Abstractions;
using FinXmlProcessor.Domain.Issues;
using FinXmlProcessor.Domain.Jobs;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FinXmlProcessor.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed history. WAL mode and a busy timeout let the desktop app and the worker share the database.
/// Migrations are idempotent and versioned; complete source records are never persisted.
/// </summary>
public sealed class SqliteProcessingRepository : IProcessingRepository, IQuarantineRepository, IFileDuplicateDetector
{
    private const int CurrentSchemaVersion = 1;
    private readonly string _connectionString;
    private readonly ILogger<SqliteProcessingRepository> _logger;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private bool _initialized;

    public SqliteProcessingRepository(string databaseFile, ILogger<SqliteProcessingRepository> logger)
    {
        DatabaseFile = databaseFile;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = 30,
            Pooling = true,
        }.ToString();
        _logger = logger;
    }

    public string DatabaseFile { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(DatabaseFile)!);
            await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA busy_timeout=30000;", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "CREATE TABLE IF NOT EXISTS schema_version(version INTEGER NOT NULL);", cancellationToken).ConfigureAwait(false);
            long version = 0;
            await using (SqliteCommand cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version;";
                version = Convert.ToInt64(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            }

            if (version < 1)
            {
                await ApplyMigration1Async(connection, cancellationToken).ConfigureAwait(false);
            }

            _initialized = true;
            _logger.LogDebug("SQLite history initialised at schema version {Version}", CurrentSchemaVersion);
        }
        finally
        {
            _initGate.Release();
        }
    }

    public static IReadOnlyList<string> Migration1Statements { get; } =
    [
        """
        CREATE TABLE IF NOT EXISTS jobs(
          id TEXT PRIMARY KEY,
          source_file_name TEXT NOT NULL,
          source_sha256 TEXT,
          source_size INTEGER NOT NULL DEFAULT 0,
          profile_id TEXT NOT NULL,
          profile_version TEXT NOT NULL,
          profile_hash TEXT NOT NULL,
          created_at TEXT NOT NULL,
          rerun_of_job_id TEXT,
          trigger TEXT NOT NULL,
          status TEXT NOT NULL,
          records_seen INTEGER NOT NULL DEFAULT 0,
          records_accepted INTEGER NOT NULL DEFAULT 0,
          records_rejected INTEGER NOT NULL DEFAULT 0,
          record_duplicates INTEGER NOT NULL DEFAULT 0,
          rows_written INTEGER NOT NULL DEFAULT 0,
          warning_count INTEGER NOT NULL DEFAULT 0,
          output_path TEXT,
          output_sha256 TEXT,
          report_path TEXT,
          business_date TEXT,
          updated_at TEXT NOT NULL);
        """,
        "CREATE INDEX IF NOT EXISTS ix_jobs_source_sha256 ON jobs(source_sha256);",
        "CREATE INDEX IF NOT EXISTS ix_jobs_created_at ON jobs(created_at);",
        "CREATE INDEX IF NOT EXISTS ix_jobs_status ON jobs(status);",
        "CREATE TABLE IF NOT EXISTS job_transitions(job_id TEXT NOT NULL, seq INTEGER NOT NULL, from_status TEXT NOT NULL, to_status TEXT NOT NULL, at TEXT NOT NULL, reason TEXT, PRIMARY KEY(job_id, seq));",
        "CREATE TABLE IF NOT EXISTS job_issues(job_id TEXT NOT NULL, seq INTEGER NOT NULL, code TEXT NOT NULL, severity TEXT NOT NULL, field_id TEXT, message TEXT NOT NULL, source_ordinal INTEGER, PRIMARY KEY(job_id, seq));",
        "CREATE TABLE IF NOT EXISTS delivery_attempts(id INTEGER PRIMARY KEY AUTOINCREMENT, job_id TEXT NOT NULL, provider TEXT NOT NULL, attempted_at TEXT NOT NULL, succeeded INTEGER NOT NULL, delivered_path TEXT, delivered_sha256 TEXT, error TEXT);",
        "CREATE INDEX IF NOT EXISTS ix_delivery_job ON delivery_attempts(job_id);",
        "CREATE TABLE IF NOT EXISTS scheduled_runs(schedule_id TEXT NOT NULL, eastern_date TEXT NOT NULL, recorded_at TEXT NOT NULL, job_id TEXT, outcome TEXT NOT NULL, note TEXT, PRIMARY KEY(schedule_id, eastern_date));",
        "CREATE INDEX IF NOT EXISTS ix_scheduled_date ON scheduled_runs(eastern_date);",
        "CREATE TABLE IF NOT EXISTS quarantine(id TEXT PRIMARY KEY, job_id TEXT, original_path TEXT NOT NULL, quarantined_path TEXT, reason_code TEXT NOT NULL, reason TEXT NOT NULL, quarantined_at TEXT NOT NULL, status TEXT NOT NULL);",
        "CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT);",
        "INSERT INTO schema_version(version) SELECT 1 WHERE NOT EXISTS (SELECT 1 FROM schema_version WHERE version = 1);",
    ];

    private static async Task ApplyMigration1Async(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using SqliteTransaction tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (string statement in Migration1Statements)
        {
            await using SqliteCommand cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = statement;
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveJobAsync(ProcessingJob job, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO jobs(id, source_file_name, source_sha256, source_size, profile_id, profile_version, profile_hash, created_at, rerun_of_job_id, trigger, status,
                                 records_seen, records_accepted, records_rejected, record_duplicates, rows_written, warning_count, output_path, output_sha256, report_path, business_date, updated_at)
                VALUES($id, $name, $sha, $size, $pid, $pver, $phash, $created, $rerun, $trigger, $status, $seen, $acc, $rej, $dup, $rows, $warn, $out, $outsha, $report, $bdate, $updated)
                ON CONFLICT(id) DO UPDATE SET source_sha256=excluded.source_sha256, source_size=excluded.source_size, status=excluded.status,
                  records_seen=excluded.records_seen, records_accepted=excluded.records_accepted, records_rejected=excluded.records_rejected,
                  record_duplicates=excluded.record_duplicates, rows_written=excluded.rows_written, warning_count=excluded.warning_count,
                  output_path=excluded.output_path, output_sha256=excluded.output_sha256, report_path=excluded.report_path, business_date=excluded.business_date, updated_at=excluded.updated_at;
                """;
            cmd.Parameters.AddWithValue("$id", job.Id.ToString("D"));
            cmd.Parameters.AddWithValue("$name", job.SourceFileName);
            cmd.Parameters.AddWithValue("$sha", (object?)job.SourceSha256 ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$size", job.SourceSizeBytes);
            cmd.Parameters.AddWithValue("$pid", job.ProfileId);
            cmd.Parameters.AddWithValue("$pver", job.ProfileVersion);
            cmd.Parameters.AddWithValue("$phash", job.ProfileHash);
            cmd.Parameters.AddWithValue("$created", Iso(job.CreatedAt));
            cmd.Parameters.AddWithValue("$rerun", (object?)job.RerunOfJobId?.ToString("D") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$trigger", job.Trigger);
            cmd.Parameters.AddWithValue("$status", job.Status.ToString());
            cmd.Parameters.AddWithValue("$seen", job.Counts.RecordsSeen);
            cmd.Parameters.AddWithValue("$acc", job.Counts.RecordsAccepted);
            cmd.Parameters.AddWithValue("$rej", job.Counts.RecordsRejected);
            cmd.Parameters.AddWithValue("$dup", job.Counts.RecordDuplicates);
            cmd.Parameters.AddWithValue("$rows", job.Counts.RowsWritten);
            cmd.Parameters.AddWithValue("$warn", job.Counts.WarningCount);
            cmd.Parameters.AddWithValue("$out", (object?)job.OutputPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$outsha", (object?)job.OutputSha256 ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$report", (object?)job.ReportPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$bdate", (object?)job.BusinessDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$updated", Iso(DateTimeOffset.UtcNow));
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await ReplaceChildRowsAsync(connection, tx, "job_transitions", job.Id, cancellationToken).ConfigureAwait(false);
        int seq = 0;
        foreach (JobStateTransition t in job.Transitions)
        {
            await using SqliteCommand cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO job_transitions(job_id, seq, from_status, to_status, at, reason) VALUES($job, $seq, $from, $to, $at, $reason);";
            cmd.Parameters.AddWithValue("$job", job.Id.ToString("D"));
            cmd.Parameters.AddWithValue("$seq", seq++);
            cmd.Parameters.AddWithValue("$from", t.From.ToString());
            cmd.Parameters.AddWithValue("$to", t.To.ToString());
            cmd.Parameters.AddWithValue("$at", Iso(t.At));
            cmd.Parameters.AddWithValue("$reason", (object?)t.Reason ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await ReplaceChildRowsAsync(connection, tx, "job_issues", job.Id, cancellationToken).ConfigureAwait(false);
        seq = 0;
        foreach (RecordIssue issue in job.Issues)
        {
            await using SqliteCommand cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO job_issues(job_id, seq, code, severity, field_id, message, source_ordinal) VALUES($job, $seq, $code, $sev, $field, $msg, $ord);";
            cmd.Parameters.AddWithValue("$job", job.Id.ToString("D"));
            cmd.Parameters.AddWithValue("$seq", seq++);
            cmd.Parameters.AddWithValue("$code", issue.Code);
            cmd.Parameters.AddWithValue("$sev", issue.Severity.ToString());
            cmd.Parameters.AddWithValue("$field", (object?)issue.FieldId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$msg", issue.Message);
            cmd.Parameters.AddWithValue("$ord", (object?)issue.SourceOrdinal ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProcessingJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        List<ProcessingJob> jobs = await ReadJobsAsync(connection, "WHERE id = $id", cmd => cmd.Parameters.AddWithValue("$id", jobId.ToString("D")), 1, cancellationToken).ConfigureAwait(false);
        return jobs.FirstOrDefault();
    }

    public async Task<IReadOnlyList<ProcessingJob>> QueryJobsAsync(JobQuery query, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var clauses = new List<string>();
        var binders = new List<Action<SqliteCommand>>();
        if (query.Status is JobStatus status)
        {
            clauses.Add("status = $status");
            binders.Add(c => c.Parameters.AddWithValue("$status", status.ToString()));
        }

        if (!string.IsNullOrWhiteSpace(query.SourceNameContains))
        {
            clauses.Add("source_file_name LIKE $name");
            binders.Add(c => c.Parameters.AddWithValue("$name", "%" + query.SourceNameContains.Replace("%", "\\%", StringComparison.Ordinal) + "%"));
        }

        if (query.CreatedAfter is DateTimeOffset after)
        {
            clauses.Add("created_at > $after");
            binders.Add(c => c.Parameters.AddWithValue("$after", Iso(after)));
        }

        string where = clauses.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", clauses);
        return await ReadJobsAsync(connection, where, cmd => binders.ForEach(b => b(cmd)), Math.Clamp(query.Limit, 1, 10_000), cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProcessingJob?> FindLatestJobBySourceHashAsync(string sha256, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        List<ProcessingJob> jobs = await ReadJobsAsync(connection, "WHERE source_sha256 = $sha AND status IN ('Completed','CompletedWithWarnings','Delivering','Delivered')", cmd => cmd.Parameters.AddWithValue("$sha", sha256), 1, cancellationToken).ConfigureAwait(false);
        return jobs.FirstOrDefault();
    }

    public async Task<FileDuplicateMatch?> FindBySha256Async(string sha256, CancellationToken cancellationToken)
    {
        ProcessingJob? job = await FindLatestJobBySourceHashAsync(sha256, cancellationToken).ConfigureAwait(false);
        return job is null ? null : new FileDuplicateMatch(job.Id, job.SourceFileName, job.FinishedAt ?? job.CreatedAt, job.Status.ToString());
    }

    public async Task RecordDeliveryAttemptAsync(DeliveryAttempt attempt, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO delivery_attempts(job_id, provider, attempted_at, succeeded, delivered_path, delivered_sha256, error) VALUES($job, $provider, $at, $ok, $path, $sha, $err);";
        cmd.Parameters.AddWithValue("$job", attempt.JobId.ToString("D"));
        cmd.Parameters.AddWithValue("$provider", attempt.Provider);
        cmd.Parameters.AddWithValue("$at", Iso(attempt.AttemptedAt));
        cmd.Parameters.AddWithValue("$ok", attempt.Succeeded ? 1 : 0);
        cmd.Parameters.AddWithValue("$path", (object?)attempt.DeliveredPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sha", (object?)attempt.DeliveredSha256 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$err", (object?)attempt.SanitizedError ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DeliveryAttempt>> GetDeliveryAttemptsAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT provider, attempted_at, succeeded, delivered_path, delivered_sha256, error FROM delivery_attempts WHERE job_id = $job ORDER BY id;";
        cmd.Parameters.AddWithValue("$job", jobId.ToString("D"));
        var result = new List<DeliveryAttempt>();
        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new DeliveryAttempt(jobId, reader.GetString(0), ParseIso(reader.GetString(1)), reader.GetInt64(2) == 1, NullableString(reader, 3), NullableString(reader, 4), NullableString(reader, 5)));
        }

        return result;
    }

    public async Task<ScheduledRunEntry?> GetScheduledRunAsync(string scheduleId, DateOnly easternDate, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT recorded_at, job_id, outcome, note FROM scheduled_runs WHERE schedule_id = $sid AND eastern_date = $date;";
        cmd.Parameters.AddWithValue("$sid", scheduleId);
        cmd.Parameters.AddWithValue("$date", easternDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        string? jobText = NullableString(reader, 1);
        return new ScheduledRunEntry(scheduleId, easternDate, ParseIso(reader.GetString(0)), jobText is null ? null : Guid.Parse(jobText), reader.GetString(2), NullableString(reader, 3));
    }

    public async Task<bool> TryRecordScheduledRunAsync(ScheduledRunEntry entry, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO scheduled_runs(schedule_id, eastern_date, recorded_at, job_id, outcome, note) VALUES($sid, $date, $at, $job, $outcome, $note);";
        BindScheduled(cmd, entry);
        int rows = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return rows == 1;
    }

    public async Task UpdateScheduledRunAsync(ScheduledRunEntry entry, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE scheduled_runs SET recorded_at = $at, job_id = $job, outcome = $outcome, note = $note WHERE schedule_id = $sid AND eastern_date = $date;";
        BindScheduled(cmd, entry);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        object? value = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : (string)value;
    }

    public async Task SetSettingAsync(string key, string? value, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO settings(key, value) VALUES($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", (object?)value ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveQuarantineEntryAsync(QuarantineEntry entry, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO quarantine(id, job_id, original_path, quarantined_path, reason_code, reason, quarantined_at, status)
            VALUES($id, $job, $orig, $qpath, $code, $reason, $at, $status)
            ON CONFLICT(id) DO UPDATE SET quarantined_path = excluded.quarantined_path, status = excluded.status;
            """;
        cmd.Parameters.AddWithValue("$id", entry.Id.ToString("D"));
        cmd.Parameters.AddWithValue("$job", (object?)entry.JobId?.ToString("D") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$orig", entry.OriginalPath);
        cmd.Parameters.AddWithValue("$qpath", (object?)entry.QuarantinedPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$code", entry.ReasonCode);
        cmd.Parameters.AddWithValue("$reason", entry.SanitizedReason);
        cmd.Parameters.AddWithValue("$at", Iso(entry.QuarantinedAt));
        cmd.Parameters.AddWithValue("$status", entry.Status);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<QuarantineEntry>> ListQuarantineAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, job_id, original_path, quarantined_path, reason_code, reason, quarantined_at, status FROM quarantine ORDER BY quarantined_at DESC LIMIT 1000;";
        var result = new List<QuarantineEntry>();
        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadQuarantine(reader));
        }

        return result;
    }

    public async Task<QuarantineEntry?> GetQuarantineEntryAsync(Guid id, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, job_id, original_path, quarantined_path, reason_code, reason, quarantined_at, status FROM quarantine WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadQuarantine(reader) : null;
    }

    /// <summary>Deletes history rows older than the cutoff. Files referenced by the rows are handled by retention separately.</summary>
    public async Task<int> DeleteJobsOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        int deleted = 0;
        foreach (string sql in new[]
        {
            "DELETE FROM job_transitions WHERE job_id IN (SELECT id FROM jobs WHERE created_at < $cutoff);",
            "DELETE FROM job_issues WHERE job_id IN (SELECT id FROM jobs WHERE created_at < $cutoff);",
            "DELETE FROM delivery_attempts WHERE job_id IN (SELECT id FROM jobs WHERE created_at < $cutoff);",
            "DELETE FROM jobs WHERE created_at < $cutoff;",
        })
        {
            await using SqliteCommand cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$cutoff", Iso(cutoff));
            deleted = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    private static QuarantineEntry ReadQuarantine(SqliteDataReader reader)
    {
        string? job = NullableString(reader, 1);
        return new QuarantineEntry(Guid.Parse(reader.GetString(0)), job is null ? null : Guid.Parse(job), reader.GetString(2), NullableString(reader, 3), reader.GetString(4), reader.GetString(5), ParseIso(reader.GetString(6)), reader.GetString(7));
    }

    private static void BindScheduled(SqliteCommand cmd, ScheduledRunEntry entry)
    {
        cmd.Parameters.AddWithValue("$sid", entry.ScheduleId);
        cmd.Parameters.AddWithValue("$date", entry.EasternDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$at", Iso(entry.RecordedAt));
        cmd.Parameters.AddWithValue("$job", (object?)entry.JobId?.ToString("D") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$outcome", entry.Outcome);
        cmd.Parameters.AddWithValue("$note", (object?)entry.Note ?? DBNull.Value);
    }

    private static async Task<List<ProcessingJob>> ReadJobsAsync(SqliteConnection connection, string where, Action<SqliteCommand> bind, int limit, CancellationToken cancellationToken)
    {
        var jobs = new List<ProcessingJob>();
        var rows = new List<JobRow>();
        await using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT id, source_file_name, source_sha256, source_size, profile_id, profile_version, profile_hash, created_at, rerun_of_job_id, trigger, status,
                       records_seen, records_accepted, records_rejected, record_duplicates, rows_written, warning_count, output_path, output_sha256, report_path, business_date
                FROM jobs {where} ORDER BY created_at DESC LIMIT {limit};
                """;
            bind(cmd);
            await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new JobRow(
                    Guid.Parse(reader.GetString(0)), reader.GetString(1), NullableString(reader, 2), reader.GetInt64(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                    ParseIso(reader.GetString(7)), NullableString(reader, 8) is { } r ? Guid.Parse(r) : null, reader.GetString(9), Enum.Parse<JobStatus>(reader.GetString(10)),
                    new ProcessingCounts(reader.GetInt64(11), reader.GetInt64(12), reader.GetInt64(13), reader.GetInt64(14), reader.GetInt64(15), reader.GetInt64(16)),
                    NullableString(reader, 17), NullableString(reader, 18), NullableString(reader, 19),
                    NullableString(reader, 20) is { } d ? DateOnly.ParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null));
            }
        }

        foreach (JobRow row in rows)
        {
            var transitions = new List<JobStateTransition>();
            await using (SqliteCommand cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT from_status, to_status, at, reason FROM job_transitions WHERE job_id = $job ORDER BY seq;";
                cmd.Parameters.AddWithValue("$job", row.Id.ToString("D"));
                await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    transitions.Add(new JobStateTransition(Enum.Parse<JobStatus>(reader.GetString(0)), Enum.Parse<JobStatus>(reader.GetString(1)), ParseIso(reader.GetString(2)), NullableString(reader, 3)));
                }
            }

            var issues = new List<RecordIssue>();
            await using (SqliteCommand cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT code, severity, field_id, message, source_ordinal FROM job_issues WHERE job_id = $job ORDER BY seq;";
                cmd.Parameters.AddWithValue("$job", row.Id.ToString("D"));
                await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    issues.Add(new RecordIssue(reader.GetString(0), Enum.Parse<IssueSeverity>(reader.GetString(1)), NullableString(reader, 2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetInt64(4)));
                }
            }

            jobs.Add(ProcessingJob.Rehydrate(row.Id, row.SourceFileName, row.SourceSha256, row.SourceSize, row.ProfileId, row.ProfileVersion, row.ProfileHash, row.CreatedAt, row.RerunOfJobId, row.Trigger, row.Status, row.Counts, row.OutputPath, row.OutputSha256, row.ReportPath, row.BusinessDate, transitions, issues));
        }

        return jobs;
    }

    private static async Task ReplaceChildRowsAsync(SqliteConnection connection, SqliteTransaction tx, string table, Guid jobId, CancellationToken cancellationToken)
    {
        await using SqliteCommand cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"DELETE FROM {table} WHERE job_id = $job;";
        cmd.Parameters.AddWithValue("$job", jobId.ToString("D"));
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA busy_timeout=30000; PRAGMA foreign_keys=ON;", cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseIso(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static string? NullableString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private sealed record JobRow(Guid Id, string SourceFileName, string? SourceSha256, long SourceSize, string ProfileId, string ProfileVersion, string ProfileHash, DateTimeOffset CreatedAt, Guid? RerunOfJobId, string Trigger, JobStatus Status, ProcessingCounts Counts, string? OutputPath, string? OutputSha256, string? ReportPath, DateOnly? BusinessDate);
}
