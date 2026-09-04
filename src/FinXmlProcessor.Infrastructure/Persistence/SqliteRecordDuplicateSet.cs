using System.Security.Cryptography;
using System.Text;
using FinXmlProcessor.Application.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FinXmlProcessor.Infrastructure.Persistence;

/// <summary>
/// Record duplicate keys are kept in a per-job SQLite file in the staging folder rather than in process memory,
/// so a multi-million-record input does not grow the working set. Keys are stored as SHA-256 digests (no raw values).
/// The file is deleted when the job finishes.
/// </summary>
public sealed class SqliteRecordDuplicateSet : IRecordDuplicateSet
{
    private const int CommitEvery = 2000;
    private readonly string _path;
    private readonly SqliteConnection _connection;
    private readonly SqliteCommand _insert;
    private readonly SqliteParameter _keyParameter;
    private SqliteTransaction _transaction;
    private int _pending;
    private bool _disposed;

    private SqliteRecordDuplicateSet(string path, SqliteConnection connection)
    {
        _path = path;
        _connection = connection;
        _transaction = connection.BeginTransaction();
        _insert = connection.CreateCommand();
        _insert.CommandText = "INSERT OR IGNORE INTO keys(h) VALUES($h);";
        _keyParameter = _insert.Parameters.Add("$h", SqliteType.Blob);
        _insert.Transaction = _transaction;
    }

    public static SqliteRecordDuplicateSet Create(string directory, Guid jobId)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"dupkeys-{jobId:N}.sqlite");
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        connection.Open();
        using (SqliteCommand pragma = connection.CreateCommand())
        {
            // Durability is irrelevant for a scratch file; speed matters.
            pragma.CommandText = "PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF; PRAGMA temp_store=MEMORY; CREATE TABLE keys(h BLOB PRIMARY KEY) WITHOUT ROWID;";
            pragma.ExecuteNonQuery();
        }

        return new SqliteRecordDuplicateSet(path, connection);
    }

    public ValueTask<bool> IsDuplicateAsync(string compositeKey, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        _keyParameter.Value = SHA256.HashData(Encoding.UTF8.GetBytes(compositeKey));
        int inserted = _insert.ExecuteNonQuery();
        if (++_pending >= CommitEvery)
        {
            _transaction.Commit();
            _transaction.Dispose();
            _transaction = _connection.BeginTransaction();
            _insert.Transaction = _transaction;
            _pending = 0;
        }

        return ValueTask.FromResult(inserted == 0);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _insert.Dispose();
        _transaction.Dispose();
        _connection.Dispose();
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            // Retention cleanup of the staging folder will remove it later.
        }

        return ValueTask.CompletedTask;
    }
}

public sealed class SqliteRecordDuplicateSetFactory : IRecordDuplicateSetFactory
{
    private readonly IAppPaths _paths;
    private readonly ILogger<SqliteRecordDuplicateSetFactory> _logger;

    public SqliteRecordDuplicateSetFactory(IAppPaths paths, ILogger<SqliteRecordDuplicateSetFactory> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public Task<IRecordDuplicateSet> CreateAsync(Guid jobId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogDebug("Creating on-disk duplicate key set for job {JobId}", jobId);
        return Task.FromResult<IRecordDuplicateSet>(SqliteRecordDuplicateSet.Create(_paths.Staging, jobId));
    }
}
