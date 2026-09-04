# Architecture

FinXml Processor is a modular monolith: one solution, no services, no containers, no web server, no remote database.
The desktop app and the headless worker compose exactly the same host (`FinXmlHost.CreateBuilder`) so behaviour is
identical whichever front end triggers a job.

## Projects and dependencies

```text
Domain ──────────┐
                 ├── Application ──┬── Processing.Xml ──┐
                 │                 ├── Output.Excel ────┼── Infrastructure ──┬── Worker (CLI)
                 │                 │                    │                    └── Desktop (Avalonia)
                 └─────────────────┴────────────────────┘
```

| Project | Responsibility | May reference |
| --- | --- | --- |
| `Domain` | Typed cells, tables/rows, issues and codes, job aggregate and state machine, masking | BCL only |
| `Application` | Contracts (`IRecordReader`, `IRecordMapper`, `IWorkbookWriter`, `IProcessingRepository`, `ISecretStore`, `IOutputDelivery`, `IInputAcquirer`, `IScheduleService`, `IBackgroundAgentManager`, …), mapping profile model/schema/loader, cell conversion and transforms, field validation, the `ProcessingPipeline`, reports | Domain, NodaTime, JsonSchema.Net, logging abstractions |
| `Processing.Xml` | Secure streaming `XmlReader`, declarative `ProfileRecordMapper`, file validation and format sniffing | Application |
| `Output.Excel` | Streaming XLSX writer, style table, sheet naming, read-side verification | Application, DocumentFormat.OpenXml |
| `Infrastructure` | SQLite history and duplicate-key spill, file lock, quarantine, JSON reports, local-folder delivery, local and SFTP acquisition, Keychain/DPAPI secret stores, NodaTime schedule, LaunchAgent manager, Serilog with redaction, diagnostics, retention, user settings, host composition | everything above, SSH.NET, Serilog |
| `Worker` | `finxml` CLI (System.CommandLine), exit codes, benchmark | Infrastructure |
| `Desktop` | Avalonia UI, view models (CommunityToolkit.Mvvm), dialogs, single-job runner | Infrastructure |

The processing engine (`Domain`, `Application`, `Processing.Xml`, `Output.Excel`) never references Avalonia,
macOS APIs, SSH.NET, SQLite or a concrete logging provider.

## Processing pipeline

`ProcessingPipeline.RunAsync` is the only processing path. Steps, in order:

1. Resolve the profile (id or path) and its mapper factory; failure returns `ConfigurationInvalid` without a job.
2. Acquire the processing lock (in-process semaphore plus exclusively opened lock file). Contention returns
   `LockUnavailable` without a job.
3. Create the job (`Discovered` → `Ready`) and persist it.
4. `Validating`: file existence/extension/size/stability, format sniffing (gzip, zip, PGP, binary), SHA-256.
   Unsupported formats quarantine the input; other file problems fail the job. Then file-level duplicate lookup:
   identical content already processed successfully blocks the run unless `--force`, in which case a warning is
   recorded and the new job is linked to the earlier one.
5. `Processing`: open the streaming reader, the mapper, the field validator, the on-disk duplicate-key set (only if
   the profile declares a key) and the workbook session. For each record: map → validate → duplicate check → write
   rows, or write a rejected line. Progress is reported at a throttled interval. Only counts, a capped issue sample
   and per-code counters are retained.
6. `GeneratingOutput`: close sheets, write the Summary sheet, assemble the package, verify it read-only, hash it,
   and move it atomically to its final name `<profile>_<businessDate>_<jobId8>.xlsx`.
7. `Completed` or `CompletedWithWarnings`; then, if a delivery provider is configured, `Delivering` → `Delivered`
   (or `Failed` with a recorded attempt).
8. Always: write the JSON report and persist the job. Cancellation → `Cancelled`; a fatal reader/writer error →
   `Failed` or `Quarantined` (the reader is released before the input is moved).

Malformed XML is detected during the single streaming pass; the input is not read twice.

## Bounded memory

- The XML reader keeps only the open-element path and one record `XElement` at a time.
- The XLSX writer streams worksheet XML through `XmlWriter` into deflate-compressed spool files and assembles the
  package with `ZipArchive` in create mode, one entry at a time. The OpenXml SDK's package layer was measured to
  buffer whole worksheet parts in memory (≈400 MB managed heap for a 200 MB input), so it is used only for the
  style table and for read-side verification.
- Record duplicate keys are SHA-256 digests in a per-job SQLite scratch file, deleted when the job ends.
- Rejection issues retained on the job are capped (`MaxRetainedRejectionIssues`); per-code counters are exact.

Measured on the 200 MB synthetic file: 20 s, 100 MB peak working set, 19 MB peak managed heap
(see `benchmarks/README.md`). The integration test `Memory_stays_bounded_for_a_generated_dataset` guards this.

## Job state model

```text
Discovered -> Ready -> Validating -> Processing -> GeneratingOutput
GeneratingOutput -> Completed | CompletedWithWarnings
Completed | CompletedWithWarnings -> Delivering -> Delivered
Any active state -> Failed | Cancelled | Quarantined
```

`JobStateMachine` rejects any other transition; `ProcessingJob.TransitionTo` records time and sanitized reason;
the SQLite repository persists transitions and issues with the job.

## Scheduling

`DailyScheduleService` computes occurrences in `America/New_York` with NodaTime (lenient resolver for skipped and
ambiguous local times). `ScheduledRunCoordinator.RunDueAsync` evaluates whether a run is due (inside the catch-up
window and not already recorded in the ledger for that Eastern date), **claims the date in the ledger first**,
acquires input (SFTP when configured, otherwise the input folder), runs the pipeline and records the outcome.
A `no-input` outcome may be retried within the window; any other outcome is final for that date.

The macOS LaunchAgent (`com.example.finxmlprocessor.worker`) runs `finxml schedule run-due --quiet` at load and every
`AgentIntervalSeconds` (default 300). launchd coalesces intervals missed during sleep, and run-due is idempotent, so
no daemon state is needed. Limitations are documented in `docs/operations-macos.md`.

## Extension points

| Contract | Shipped implementation | How to add another |
| --- | --- | --- |
| `IRecordMapperFactory` | `profile` (declarative) | Implement for a new `mapperType`, register it, set `mapperType` in the profile |
| `IInputAcquirer` | `local`, `sftp` | Implement, register; the coordinator prefers a configured non-local provider |
| `IOutputDelivery` | `local-folder` | Derive from `InternalSystemDeliveryBase`, implement `TransmitAsync`, register, set `Delivery:Provider`; inherit `DeliveryContractTests` |
| `ISecretStore` | Keychain (macOS), DPAPI (Windows), in-memory (tests) | Implement, register per platform |
| `IWorkbookWriter` | streaming XLSX | Implement for another format; the pipeline is format-agnostic |
| `IBackgroundAgentManager` | LaunchAgent (macOS), no-op elsewhere | Implement for another supervisor |

### Adding a real XML adapter

Most real schemas fit the declarative profile (namespaces, nested element/attribute paths, transforms, typed columns,
validation, duplicate key). When a record must fan out into several related sheets or needs logic the profile cannot
express, implement `IRecordMapperFactory` with a new `MapperType`, return one `OutputRow` per table in table order,
and keep the `SourceRecordEnvelope` unreferenced after `Map` returns. Tests in `Processing.Xml.Tests` show the
expected shape.
