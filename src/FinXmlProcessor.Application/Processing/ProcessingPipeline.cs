using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using FinXmlProcessor.Application.Abstractions;
using FinXmlProcessor.Application.Naming;
using FinXmlProcessor.Application.Profiles;
using FinXmlProcessor.Application.Reports;
using FinXmlProcessor.Application.Scheduling;
using FinXmlProcessor.Application.Validation;
using FinXmlProcessor.Domain.Cells;
using FinXmlProcessor.Domain.Issues;
using FinXmlProcessor.Domain.Jobs;
using FinXmlProcessor.Domain.Security;
using FinXmlProcessor.Domain.Sources;
using FinXmlProcessor.Domain.Tables;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FinXmlProcessor.Application.Processing;

/// <summary>
/// The single processing path shared by the desktop app, the CLI and the scheduled worker. Streams records from
/// the reader through mapping, validation and duplicate detection into the workbook session, keeping only
/// counts and a bounded sample of issues in memory.
/// </summary>
public sealed class ProcessingPipeline
{
    private readonly IProfileRegistry _profiles;
    private readonly IInputValidator _inputValidator;
    private readonly IFileDuplicateDetector _fileDuplicates;
    private readonly IRecordReaderFactory _readerFactory;
    private readonly IReadOnlyList<IRecordMapperFactory> _mapperFactories;
    private readonly IRecordDuplicateSetFactory _duplicateSetFactory;
    private readonly IWorkbookWriter _workbookWriter;
    private readonly IProcessingRepository _repository;
    private readonly IReportWriter _reportWriter;
    private readonly IQuarantineService _quarantine;
    private readonly IProcessingLock _lock;
    private readonly IReadOnlyList<IOutputDelivery> _deliveries;
    private readonly IAppPaths _paths;
    private readonly IProcessingClock _clock;
    private readonly IOptionsMonitor<ProcessingOptions> _options;
    private readonly ILogger<ProcessingPipeline> _logger;

    public ProcessingPipeline(
        IProfileRegistry profiles,
        IInputValidator inputValidator,
        IFileDuplicateDetector fileDuplicates,
        IRecordReaderFactory readerFactory,
        IEnumerable<IRecordMapperFactory> mapperFactories,
        IRecordDuplicateSetFactory duplicateSetFactory,
        IWorkbookWriter workbookWriter,
        IProcessingRepository repository,
        IReportWriter reportWriter,
        IQuarantineService quarantine,
        IProcessingLock processingLock,
        IEnumerable<IOutputDelivery> deliveries,
        IAppPaths paths,
        IProcessingClock clock,
        IOptionsMonitor<ProcessingOptions> options,
        ILogger<ProcessingPipeline> logger)
    {
        _profiles = profiles;
        _inputValidator = inputValidator;
        _fileDuplicates = fileDuplicates;
        _readerFactory = readerFactory;
        _mapperFactories = mapperFactories.ToList();
        _duplicateSetFactory = duplicateSetFactory;
        _workbookWriter = workbookWriter;
        _repository = repository;
        _reportWriter = reportWriter;
        _quarantine = quarantine;
        _lock = processingLock;
        _deliveries = deliveries.ToList();
        _paths = paths;
        _clock = clock;
        _options = options;
        _logger = logger;
    }

    public async Task<ProcessingResult> RunAsync(ProcessingRequest request, IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken)
    {
        ProcessingOptions options = _options.CurrentValue;
        string profileId = request.ProfileId ?? options.ActiveProfileId;
        ProfileValidationResult profileResult = await _profiles.GetByIdAsync(profileId, cancellationToken).ConfigureAwait(false);
        if (!profileResult.IsValid || profileResult.Profile is null)
        {
            string message = $"Profile '{profileId}' is not usable: {string.Join("; ", profileResult.Errors.Take(3))}";
            _logger.LogError("{Message}", message);
            return new ProcessingResult(ProcessingOutcome.ConfigurationInvalid, null, null, null, null, message);
        }

        CompiledProfile profile = profileResult.Profile;
        IRecordMapperFactory? mapperFactory = _mapperFactories.FirstOrDefault(f => string.Equals(f.MapperType, profile.MapperType, StringComparison.Ordinal));
        if (mapperFactory is null)
        {
            string message = $"Profile '{profile.Id}' requires mapper type '{profile.MapperType}' which is not installed.";
            return new ProcessingResult(ProcessingOutcome.ConfigurationInvalid, null, null, null, null, message);
        }

        await using IAsyncDisposable? lease = await _lock.TryAcquireAsync($"{request.Trigger}:{OutputNaming.SafeFileNameFromPath(request.InputPath)}", cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            string? holder = await _lock.DescribeHolderAsync(cancellationToken).ConfigureAwait(false);
            string message = $"Another processing job is running{(holder is null ? string.Empty : $" ({holder})")}. Only one job may run at a time.";
            return new ProcessingResult(ProcessingOutcome.LockUnavailable, null, null, null, null, message);
        }

        DateOnly businessDate = request.BusinessDate ?? BusinessCalendar.BusinessDateFor(_clock.GetCurrentInstant());
        var job = new ProcessingJob(Guid.NewGuid(), OutputNaming.SafeFileNameFromPath(request.InputPath), null, profile.Id, profile.Version, profile.Hash, _clock.UtcNowOffset, request.RerunOfJobId, request.Trigger)
        {
            BusinessDate = businessDate,
        };
        await _repository.SaveJobAsync(job, cancellationToken).ConfigureAwait(false);

        var run = new RunState(job, profile, request, options, mapperFactory, progress);
        using var scope = _logger.BeginScope(new Dictionary<string, object> { ["JobId"] = job.Id, ["ProfileId"] = profile.Id });
        _logger.LogInformation("Job {JobId} created for {SourceFile} using profile {ProfileId} {ProfileVersion} ({Trigger})", job.Id, job.SourceFileName, profile.Id, profile.Version, request.Trigger);

        try
        {
            await Transition(run, JobStatus.Ready, null, cancellationToken).ConfigureAwait(false);
            ProcessingResult? early = await ValidateAndHashAsync(run, cancellationToken).ConfigureAwait(false);
            if (early is not null)
            {
                return early;
            }

            await ProcessRecordsAsync(run, cancellationToken).ConfigureAwait(false);
            await FinalizeOutputAsync(run, cancellationToken).ConfigureAwait(false);
            await DeliverAsync(run, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Job {JobId} cancelled by request", job.Id);
            run.Outcome = ProcessingOutcome.Cancelled;
            job.AddIssue(RecordIssue.Fatal(IssueCodes.JobCancelled, "Processing was cancelled."));
            await SafeTransition(run, JobStatus.Cancelled, "Cancelled by request").ConfigureAwait(false);
        }
        catch (ProcessingFatalException ex)
        {
            _logger.LogError(ex, "Job {JobId} failed with {Code}: {Message}", job.Id, ex.Code, ex.Message);
            job.AddIssue(RecordIssue.Fatal(ex.Code, ex.Message));
            if (ex.Quarantine)
            {
                run.Outcome = ProcessingOutcome.Quarantined;
                await QuarantineAsync(run, ex.Code, ex.Message).ConfigureAwait(false);
            }
            else
            {
                run.Outcome = ProcessingOutcome.Failed;
                await SafeTransition(run, JobStatus.Failed, $"{ex.Code}: {ex.Message}").ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // Never surface the raw exception text: it may include paths or data. The job id is the error reference.
            _logger.LogError(ex, "Job {JobId} failed unexpectedly", job.Id);
            run.Outcome = ProcessingOutcome.Failed;
            job.AddIssue(RecordIssue.Fatal(IssueCodes.JobUnexpectedError, $"Unexpected {ex.GetType().Name}. Error reference {OutputNaming.ShortJobId(job.Id)}; see the log for details."));
            await SafeTransition(run, JobStatus.Failed, $"{IssueCodes.JobUnexpectedError}: {ex.GetType().Name}").ConfigureAwait(false);
        }
        finally
        {
            await run.DisposeAsync().ConfigureAwait(false);
        }

        run.Stopwatch.Stop();
        ProcessingReport report = BuildReport(run);
        string? reportPath = null;
        try
        {
            reportPath = await _reportWriter.WriteAsync(report, CancellationToken.None).ConfigureAwait(false);
            job.ReportPath = reportPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {JobId}: report could not be written", job.Id);
        }

        await _repository.SaveJobAsync(job, CancellationToken.None).ConfigureAwait(false);
        string summary = SummaryMessage(run);
        _logger.LogInformation("Job {JobId} finished: {Outcome} in {ElapsedMs} ms. Seen {Seen}, accepted {Accepted}, rejected {Rejected}, duplicates {Duplicates}", job.Id, run.Outcome, run.Stopwatch.ElapsedMilliseconds, job.Counts.RecordsSeen, job.Counts.RecordsAccepted, job.Counts.RecordsRejected, job.Counts.RecordDuplicates);
        return new ProcessingResult(run.Outcome, job, report, job.OutputPath, reportPath, summary);
    }

    private async Task<ProcessingResult?> ValidateAndHashAsync(RunState run, CancellationToken cancellationToken)
    {
        ProcessingJob job = run.Job;
        await Transition(run, JobStatus.Validating, null, cancellationToken).ConfigureAwait(false);
        var sw = Stopwatch.StartNew();
        InputValidationResult validation = await _inputValidator.ValidateFileAsync(run.Request.InputPath, cancellationToken).ConfigureAwait(false);
        run.ValidationMs = sw.ElapsedMilliseconds;
        job.SourceSizeBytes = validation.SizeBytes;
        foreach (RecordIssue issue in validation.Issues)
        {
            job.AddIssue(issue);
        }

        if (!validation.IsValid)
        {
            RecordIssue fatal = validation.Issues.First(i => i.Severity == IssueSeverity.Fatal);
            bool quarantine = fatal.Code is IssueCodes.FileUnsupportedFormat or IssueCodes.FileEmpty or IssueCodes.FileUnsupportedExtension;
            if (quarantine)
            {
                run.Outcome = ProcessingOutcome.Quarantined;
                await QuarantineAsync(run, fatal.Code, fatal.Message).ConfigureAwait(false);
            }
            else
            {
                run.Outcome = ProcessingOutcome.Failed;
                await SafeTransition(run, JobStatus.Failed, $"{fatal.Code}: {fatal.Message}").ConfigureAwait(false);
            }

            return await FinishEarlyAsync(run).ConfigureAwait(false);
        }

        job.SetSourceHash(validation.Sha256!);
        FileDuplicateMatch? duplicate = await _fileDuplicates.FindBySha256Async(validation.Sha256!, cancellationToken).ConfigureAwait(false);
        if (duplicate is not null)
        {
            if (!run.Request.Force)
            {
                string message = $"Identical file content was already processed as job {OutputNaming.ShortJobId(duplicate.JobId)} ({duplicate.SourceFileName}, {duplicate.Status}) on {duplicate.ProcessedAt:yyyy-MM-dd HH:mm} UTC. Use force to rerun.";
                job.AddIssue(RecordIssue.Fatal(IssueCodes.FileDuplicate, message));
                run.Outcome = ProcessingOutcome.DuplicateBlocked;
                await SafeTransition(run, JobStatus.Failed, $"{IssueCodes.FileDuplicate}: duplicate of {duplicate.JobId}").ConfigureAwait(false);
                return await FinishEarlyAsync(run).ConfigureAwait(false);
            }

            job.AddIssue(RecordIssue.Warning(IssueCodes.FileDuplicate, null, $"Forced rerun of content previously processed as job {OutputNaming.ShortJobId(duplicate.JobId)}."));
        }

        await _repository.SaveJobAsync(job, cancellationToken).ConfigureAwait(false);
        return null;
    }

    private async Task<ProcessingResult> FinishEarlyAsync(RunState run)
    {
        await run.DisposeAsync().ConfigureAwait(false);
        run.Stopwatch.Stop();
        ProcessingReport report = BuildReport(run);
        string? reportPath = null;
        try
        {
            reportPath = await _reportWriter.WriteAsync(report, CancellationToken.None).ConfigureAwait(false);
            run.Job.ReportPath = reportPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {JobId}: report could not be written", run.Job.Id);
        }

        await _repository.SaveJobAsync(run.Job, CancellationToken.None).ConfigureAwait(false);
        return new ProcessingResult(run.Outcome, run.Job, report, null, reportPath, SummaryMessage(run));
    }

    private async Task ProcessRecordsAsync(RunState run, CancellationToken cancellationToken)
    {
        ProcessingJob job = run.Job;
        CompiledProfile profile = run.Profile;
        ProcessingOptions options = run.Options;
        await Transition(run, JobStatus.Processing, null, cancellationToken).ConfigureAwait(false);

        string outputDirectory = run.Request.OutputDirectory ?? options.OutputDirectory ?? _paths.DefaultOutput;
        Directory.CreateDirectory(outputDirectory);
        string finalPath = Path.Combine(outputDirectory, OutputNaming.WorkbookFileName(profile.Id, job.BusinessDate!.Value, job.Id));

        IRecordMapper mapper = run.MapperFactory.Create(profile);
        var validator = new ProfileRecordValidator(profile);
        if (profile.HasDuplicateKey)
        {
            run.DuplicateSet = await _duplicateSetFactory.CreateAsync(job.Id, cancellationToken).ConfigureAwait(false);
        }

        var writerOptions = new WorkbookWriterOptions(options.MaxRowsPerSheet, IncludeRejectedSheet: options.IncludeRejectedSheet);
        run.Session = await _workbookWriter.BeginAsync(finalPath, profile.Tables, writerOptions, cancellationToken).ConfigureAwait(false);
        run.Reader = _readerFactory.Create(run.Request.InputPath, profile);
        run.FinalPath = finalPath;

        var issues = new List<RecordIssue>(8);
        var parseWatch = Stopwatch.StartNew();
        long lastProgressTicks = 0;
        long progressInterval = TimeSpan.FromMilliseconds(options.ProgressIntervalMilliseconds).Ticks;

        await foreach (SourceRecordEnvelope envelope in run.Reader.ReadRecordsAsync(cancellationToken).ConfigureAwait(false))
        {
            run.RecordsSeen++;
            issues.Clear();
            MappedRecord mapped = mapper.Map(envelope);
            issues.AddRange(mapped.Issues);
            if (!mapped.IsRejected)
            {
                validator.Validate(mapped.Rows, envelope.SourceOrdinal, issues);
            }

            bool rejected = issues.Any(i => i.Severity >= IssueSeverity.RecordRejected);
            if (!rejected && run.DuplicateSet is not null)
            {
                string key = BuildDuplicateKey(profile, mapped.Rows);
                if (await run.DuplicateSet.IsDuplicateAsync(key, cancellationToken).ConfigureAwait(false))
                {
                    run.RecordDuplicates++;
                    issues.Add(RecordIssue.Rejection(IssueCodes.ValRecordDuplicate, null, "Record duplicates an earlier record with the same key.", envelope.SourceOrdinal));
                    rejected = true;
                }
            }

            if (rejected)
            {
                run.RecordsRejected++;
                RecordRejection(run, mapped, issues, envelope.SourceOrdinal);
            }
            else
            {
                run.RecordsAccepted++;
                foreach (OutputRow row in mapped.Rows)
                {
                    run.Session.WriteRow(row);
                }

                foreach (RecordIssue warning in issues)
                {
                    RecordWarning(run, warning);
                }
            }

            long nowTicks = run.Stopwatch.Elapsed.Ticks;
            if (nowTicks - lastProgressTicks >= progressInterval)
            {
                lastProgressTicks = nowTicks;
                run.ReportProgress(JobStatus.Processing);
            }
        }

        run.ParsingMs = parseWatch.ElapsedMilliseconds;
        run.ReportProgress(JobStatus.Processing);
        if (run.RecordsSeen == 0)
        {
            job.AddIssue(RecordIssue.Warning(IssueCodes.XmlRecordPathNotFound, null, $"No records were found at the configured record path '{string.Join("/", profile.Source.RecordPath)}'."));
            run.WarningCount++;
        }
    }

    private async Task FinalizeOutputAsync(RunState run, CancellationToken cancellationToken)
    {
        ProcessingJob job = run.Job;
        await Transition(run, JobStatus.GeneratingOutput, null, cancellationToken).ConfigureAwait(false);
        run.ReportProgress(JobStatus.GeneratingOutput);
        var sw = Stopwatch.StartNew();
        job.Counts = run.SnapshotCounts();
        IReadOnlyList<SummaryEntry> summary = BuildSummary(run);
        string finalPath = await run.Session!.CompleteAsync(summary, job.Issues, cancellationToken).ConfigureAwait(false);
        run.Session = null;
        IReadOnlyList<RecordIssue> verification = await _workbookWriter.VerifyAsync(finalPath, cancellationToken).ConfigureAwait(false);
        if (verification.Any(v => v.Severity == IssueSeverity.Fatal))
        {
            TryDelete(finalPath);
            throw new ProcessingFatalException(IssueCodes.OutputPackageInvalid, "The generated workbook failed structural validation and was discarded.");
        }

        run.WorkbookMs = sw.ElapsedMilliseconds;
        job.OutputPath = finalPath;
        job.OutputSha256 = await ComputeSha256Async(finalPath, cancellationToken).ConfigureAwait(false);
        job.Counts = run.SnapshotCounts();
        bool warnings = job.Counts.RecordsRejected > 0 || job.Counts.RecordDuplicates > 0 || job.Counts.WarningCount > 0 || job.Issues.Any(i => i.Severity == IssueSeverity.Warning);
        run.Outcome = warnings ? ProcessingOutcome.CompletedWithWarnings : ProcessingOutcome.Completed;
        await Transition(run, warnings ? JobStatus.CompletedWithWarnings : JobStatus.Completed, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task DeliverAsync(RunState run, CancellationToken cancellationToken)
    {
        if (!run.Options.DeliverAutomatically)
        {
            return;
        }

        IOutputDelivery? delivery = _deliveries.FirstOrDefault(d => d.IsConfigured);
        if (delivery is null || run.Job.OutputPath is null)
        {
            return;
        }

        ProcessingJob job = run.Job;
        await Transition(run, JobStatus.Delivering, delivery.ProviderId, cancellationToken).ConfigureAwait(false);
        run.ReportProgress(JobStatus.Delivering);
        var sw = Stopwatch.StartNew();
        DeliveryResult result;
        try
        {
            result = await delivery.DeliverAsync(job, job.OutputPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {JobId}: delivery provider {Provider} threw", job.Id, delivery.ProviderId);
            result = DeliveryResult.Failure($"Delivery provider '{delivery.ProviderId}' failed with {ex.GetType().Name}.");
        }

        run.DeliveryMs = sw.ElapsedMilliseconds;
        run.Delivery = new ProcessingReport.DeliveryInfo
        {
            Provider = delivery.ProviderId,
            Succeeded = result.Succeeded,
            DeliveredPath = result.DeliveredPath,
            DeliveredSha256 = result.DeliveredSha256,
            Error = result.SanitizedError,
        };
        await _repository.RecordDeliveryAttemptAsync(new DeliveryAttempt(job.Id, delivery.ProviderId, _clock.UtcNowOffset, result.Succeeded, result.DeliveredPath, result.DeliveredSha256, result.SanitizedError), CancellationToken.None).ConfigureAwait(false);
        if (result.Succeeded)
        {
            await Transition(run, JobStatus.Delivered, result.DeliveredPath, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            job.AddIssue(RecordIssue.Fatal(IssueCodes.JobDeliveryFailed, result.SanitizedError ?? "Delivery failed."));
            run.Outcome = ProcessingOutcome.Failed;
            await SafeTransition(run, JobStatus.Failed, $"{IssueCodes.JobDeliveryFailed}: {result.SanitizedError}").ConfigureAwait(false);
        }
    }

    private async Task QuarantineAsync(RunState run, string code, string message)
    {
        try
        {
            bool managed = IsAppManagedPath(run.Request.InputPath);
            QuarantineEntry entry = await _quarantine.QuarantineAsync(run.Job.Id, run.Request.InputPath, code, message, managed, CancellationToken.None).ConfigureAwait(false);
            await SafeTransition(run, JobStatus.Quarantined, $"{code}: {message}").ConfigureAwait(false);
            _logger.LogWarning("Job {JobId}: input quarantined ({Code}) as entry {EntryId}", run.Job.Id, code, entry.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {JobId}: quarantine failed; marking job failed instead", run.Job.Id);
            run.Outcome = ProcessingOutcome.Failed;
            await SafeTransition(run, JobStatus.Failed, $"{code}: {message} (quarantine failed)").ConfigureAwait(false);
        }
    }

    private bool IsAppManagedPath(string path)
    {
        string full = Path.GetFullPath(path);
        string staging = Path.GetFullPath(_paths.Staging);
        string input = Path.GetFullPath(_options.CurrentValue.InputDirectory ?? _paths.DefaultInput);
        return IsUnder(full, staging) || IsUnder(full, input);

        static bool IsUnder(string candidate, string root)
        {
            string r = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return candidate.StartsWith(r, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
    }

    private static void RecordRejection(RunState run, MappedRecord mapped, List<RecordIssue> issues, long sourceOrdinal)
    {
        var codes = new List<string>(issues.Count);
        var messages = new List<string>(issues.Count);
        foreach (RecordIssue issue in issues)
        {
            run.CountIssue(issue.Code);
            if (issue.Severity >= IssueSeverity.RecordRejected)
            {
                codes.Add(issue.Code);
                messages.Add(issue.FieldId is null ? issue.Message : $"{issue.FieldId}: {issue.Message}");
                if (run.RetainedRejections < run.Options.MaxRetainedRejectionIssues)
                {
                    run.Job.AddIssue(issue);
                    run.RetainedRejections++;
                }
                else
                {
                    run.IssuesTruncated = true;
                }
            }
        }

        if (run.Session is not null && run.Options.IncludeRejectedSheet && run.RejectedSheetRows < run.Options.MaxRejectedSheetRows)
        {
            run.RejectedSheetRows++;
            var safeFields = new List<KeyValuePair<string, string>>();
            foreach (OutputRow row in mapped.Rows)
            {
                OutputTableDefinition table = run.Profile.TableById(row.TableId);
                for (int i = 0; i < table.Columns.Count && i < row.Cells.Count; i++)
                {
                    OutputColumnDefinition column = table.Columns[i];
                    CellValue cell = row.Cells[i];
                    if (!column.IsSafeForRejectionOutput || cell.IsBlank)
                    {
                        continue;
                    }

                    safeFields.Add(new KeyValuePair<string, string>(column.Heading, Masking.ForClassification(cell.ToInvariantString(), column.Sensitivity)));
                }
            }

            run.Session.WriteRejected(new RejectedRecordLine(sourceOrdinal, mapped.SafeIdentifier, string.Join(";", codes), string.Join(" | ", messages), safeFields));
        }
    }

    private static void RecordWarning(RunState run, RecordIssue warning)
    {
        run.CountIssue(warning.Code);
        run.WarningCount++;
        if (run.RetainedWarnings < run.Options.MaxRetainedRejectionIssues)
        {
            run.Job.AddIssue(warning);
            run.RetainedWarnings++;
        }
        else
        {
            run.IssuesTruncated = true;
        }
    }

    private static string BuildDuplicateKey(CompiledProfile profile, IReadOnlyList<OutputRow> rows)
    {
        var parts = new string[profile.DuplicateKeyFieldIndexes.Count];
        for (int i = 0; i < parts.Length; i++)
        {
            CompiledField field = profile.Fields[profile.DuplicateKeyFieldIndexes[i]];
            parts[i] = rows[field.TableIndex].Cells[field.ColumnIndex].ToInvariantString();
        }

        return string.Join('', parts);
    }

    private IReadOnlyList<SummaryEntry> BuildSummary(RunState run)
    {
        ProcessingJob job = run.Job;
        ProcessingCounts counts = job.Counts;
        var entries = new List<SummaryEntry>
        {
            new("Source file", job.SourceFileName),
            new("Source size (bytes)", job.SourceSizeBytes.ToString(CultureInfo.InvariantCulture)),
            new("Source SHA-256", job.SourceSha256 ?? string.Empty),
            new("Profile", $"{run.Profile.Id} {run.Profile.Version}"),
            new("Profile SHA-256", run.Profile.Hash),
            new("Job ID", job.Id.ToString("D")),
            new("Business date", job.BusinessDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty),
            new("Trigger", job.Trigger),
            new("Started (UTC)", job.StartedAt?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? string.Empty),
            new("Generated (UTC)", _clock.UtcNowOffset.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
            new("Records seen", counts.RecordsSeen.ToString(CultureInfo.InvariantCulture)),
            new("Records accepted", counts.RecordsAccepted.ToString(CultureInfo.InvariantCulture)),
            new("Records rejected", counts.RecordsRejected.ToString(CultureInfo.InvariantCulture)),
            new("Record duplicates", counts.RecordDuplicates.ToString(CultureInfo.InvariantCulture)),
            new("Rows written", counts.RowsWritten.ToString(CultureInfo.InvariantCulture)),
            new("Warnings", counts.WarningCount.ToString(CultureInfo.InvariantCulture)),
            new("Status", counts.RecordsRejected > 0 || counts.RecordDuplicates > 0 || counts.WarningCount > 0 ? "Completed with warnings" : "Completed"),
            new("Application", $"{AppInfo.ProductName} {AppInfo.Version}"),
        };
        if (run.Profile.IsSynthetic)
        {
            entries.Add(new SummaryEntry("Note", "Synthetic demo profile: mapping and validation rules are placeholders, not approved business rules."));
        }

        if (job.RerunOfJobId is Guid rerun)
        {
            entries.Add(new SummaryEntry("Rerun of job", rerun.ToString("D")));
        }

        return entries;
    }

    private ProcessingReport BuildReport(RunState run)
    {
        ProcessingJob job = run.Job;
        job.Counts = run.SnapshotCounts();
        var report = new ProcessingReport
        {
            JobId = job.Id,
            RerunOfJobId = job.RerunOfJobId,
            Status = job.Status,
            Outcome = run.Outcome.ToString(),
            Trigger = job.Trigger,
            BusinessDate = job.BusinessDate,
            Source = new ProcessingReport.SourceInfo { FileName = job.SourceFileName, SizeBytes = job.SourceSizeBytes, Sha256 = job.SourceSha256, Provider = run.Request.SourceProvider },
            Profile = new ProcessingReport.ProfileInfo { Id = run.Profile.Id, Version = run.Profile.Version, Hash = run.Profile.Hash, IsSynthetic = run.Profile.IsSynthetic },
            Times = new ProcessingReport.Timestamps { CreatedUtc = job.CreatedAt, StartedUtc = job.StartedAt, FinishedUtc = job.FinishedAt ?? _clock.UtcNowOffset },
            TimingsMs = new ProcessingReport.Timings
            {
                Acquisition = run.AcquisitionMs,
                Validation = run.ValidationMs,
                ParsingAndMapping = run.ParsingMs,
                WorkbookWrite = run.WorkbookMs,
                Delivery = run.DeliveryMs,
                Total = run.Stopwatch.ElapsedMilliseconds,
            },
            Counts = job.Counts,
            Delivery = run.Delivery,
            IssueCodeCounts = new Dictionary<string, long>(run.IssueCodeCounts, StringComparer.Ordinal),
            IssuesTruncated = run.IssuesTruncated,
            Application = new ProcessingReport.ApplicationInfo { Name = AppInfo.ProductName, Version = AppInfo.Version, Platform = AppInfo.Platform },
        };

        if (job.OutputPath is not null)
        {
            long size = 0;
            try
            {
                size = new FileInfo(job.OutputPath).Length;
            }
            catch (IOException)
            {
            }

            report.Output = new ProcessingReport.OutputInfo { Path = job.OutputPath, Sha256 = job.OutputSha256, SizeBytes = size, Sheets = run.Profile.Tables.Select(t => t.SheetName).ToList() };
        }

        report.Issues.AddRange(job.Issues.Where(i => i.Severity >= IssueSeverity.Warning).OrderByDescending(i => i.Severity).Take(run.Options.MaxRetainedRejectionIssues).Select(ProcessingReport.ReportIssue.From));
        if (run.Profile.IsSynthetic)
        {
            report.Notes.Add("Synthetic demo profile: rules are placeholders, not approved financial processing rules.");
        }

        return report;
    }

    private static string SummaryMessage(RunState run)
    {
        ProcessingCounts c = run.Job.Counts;
        return run.Outcome switch
        {
            ProcessingOutcome.Completed => $"Completed: {c.RecordsAccepted} records accepted, {c.RowsWritten} rows written.",
            ProcessingOutcome.CompletedWithWarnings => $"Completed with warnings: {c.RecordsAccepted} accepted, {c.RecordsRejected} rejected, {c.RecordDuplicates} duplicates, {c.WarningCount} warnings.",
            ProcessingOutcome.Cancelled => "Cancelled. No output was published.",
            ProcessingOutcome.DuplicateBlocked => run.Job.Issues.LastOrDefault(i => i.Code == IssueCodes.FileDuplicate)?.Message ?? "Duplicate file.",
            ProcessingOutcome.Quarantined => "Input quarantined: " + (run.Job.Issues.LastOrDefault(i => i.Severity == IssueSeverity.Fatal)?.Message ?? "unusable input."),
            _ => "Failed: " + (run.Job.Issues.LastOrDefault(i => i.Severity == IssueSeverity.Fatal)?.Message ?? "see report."),
        };
    }

    private async Task Transition(RunState run, JobStatus status, string? reason, CancellationToken cancellationToken)
    {
        run.Job.TransitionTo(status, _clock.UtcNowOffset, reason);
        await _repository.SaveJobAsync(run.Job, cancellationToken).ConfigureAwait(false);
    }

    private async Task SafeTransition(RunState run, JobStatus status, string? reason)
    {
        try
        {
            if (JobStateMachine.CanTransition(run.Job.Status, status))
            {
                run.Job.TransitionTo(status, _clock.UtcNowOffset, reason);
            }

            await _repository.SaveJobAsync(run.Job, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {JobId}: could not persist transition to {Status}", run.Job.Id, status);
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not delete {Path}", path);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Could not delete {Path}", path);
        }
    }

    /// <summary>Mutable per-run state. Everything here is O(1) in record count except the capped issue sample.</summary>
    private sealed class RunState : IAsyncDisposable
    {
        public RunState(ProcessingJob job, CompiledProfile profile, ProcessingRequest request, ProcessingOptions options, IRecordMapperFactory mapperFactory, IProgress<ProcessingProgress>? progress)
        {
            Job = job;
            Profile = profile;
            Request = request;
            Options = options;
            MapperFactory = mapperFactory;
            Progress = progress;
        }

        public ProcessingJob Job { get; }

        public CompiledProfile Profile { get; }

        public ProcessingRequest Request { get; }

        public ProcessingOptions Options { get; }

        public IRecordMapperFactory MapperFactory { get; }

        public IProgress<ProcessingProgress>? Progress { get; }

        public Stopwatch Stopwatch { get; } = Stopwatch.StartNew();

        public ProcessingOutcome Outcome { get; set; } = ProcessingOutcome.Failed;

        public IRecordReader? Reader { get; set; }

        public IWorkbookSession? Session { get; set; }

        public IRecordDuplicateSet? DuplicateSet { get; set; }

        public string? FinalPath { get; set; }

        public long RecordsSeen { get; set; }

        public long RecordsAccepted { get; set; }

        public long RecordsRejected { get; set; }

        public long RecordDuplicates { get; set; }

        public long WarningCount { get; set; }

        public int RetainedRejections { get; set; }

        public int RetainedWarnings { get; set; }

        public int RejectedSheetRows { get; set; }

        public bool IssuesTruncated { get; set; }

        public long AcquisitionMs { get; set; }

        public long ValidationMs { get; set; }

        public long ParsingMs { get; set; }

        public long WorkbookMs { get; set; }

        public long DeliveryMs { get; set; }

        public ProcessingReport.DeliveryInfo? Delivery { get; set; }

        public Dictionary<string, long> IssueCodeCounts { get; } = new(StringComparer.Ordinal);

        public void CountIssue(string code)
        {
            IssueCodeCounts[code] = IssueCodeCounts.TryGetValue(code, out long n) ? n + 1 : 1;
        }

        public ProcessingCounts SnapshotCounts() => new(RecordsSeen, RecordsAccepted, RecordsRejected, RecordDuplicates, Session?.RowsWritten ?? Job.Counts.RowsWritten, WarningCount);

        public void ReportProgress(JobStatus phase)
        {
            Progress?.Report(new ProcessingProgress(phase, Reader?.BytesRead ?? 0, Reader?.TotalBytes, RecordsSeen, RecordsAccepted, RecordsRejected, RecordDuplicates, Session?.RowsWritten ?? 0, Stopwatch.Elapsed));
        }

        public async ValueTask DisposeAsync()
        {
            if (Session is not null)
            {
                // Not completed: the session discards its staging file.
                Job.Counts = SnapshotCounts();
                await Session.DisposeAsync().ConfigureAwait(false);
                Session = null;
            }

            if (Reader is not null)
            {
                await Reader.DisposeAsync().ConfigureAwait(false);
                Reader = null;
            }

            if (DuplicateSet is not null)
            {
                await DuplicateSet.DisposeAsync().ConfigureAwait(false);
                DuplicateSet = null;
            }
        }
    }
}
