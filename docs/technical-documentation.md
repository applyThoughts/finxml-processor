# Technical documentation

## Stack

- .NET 10 (C#), Avalonia 11 desktop UI with CommunityToolkit.Mvvm, System.CommandLine worker.
- `System.Xml.XmlReader` for streaming XML; DocumentFormat.OpenXml for the workbook style table and read-side
  verification; a streaming zip writer for worksheet data.
- SQLite (Microsoft.Data.Sqlite) for history; NodaTime for time-zone arithmetic; SSH.NET for SFTP;
  Serilog for structured logging; macOS Keychain or Windows DPAPI for secrets.
- xUnit, FluentAssertions 7, NSubstitute and Avalonia headless tests.

## Solution layout

| Project | Role |
| --- | --- |
| `Domain` | Typed cells, output tables and rows, issue codes, job aggregate and state machine, masking |
| `Application` | Contracts, mapping profile model + JSON schema + loader, cell conversion, transforms, field validation, the processing pipeline, report model |
| `Processing.Xml` | Secure streaming reader, declarative record mapper, file validation and format sniffing |
| `Output.Excel` | Streaming XLSX writer, style table, sheet naming, verification |
| `Infrastructure` | SQLite repository and duplicate-key spill, file lock, quarantine, reports, delivery, local and SFTP acquisition, secret stores, schedule, LaunchAgent manager, logging redaction, diagnostics, retention, settings, host composition |
| `Worker` | `finxml` CLI and exit codes |
| `Desktop` | Avalonia views and view models |

The engine projects (Domain, Application, Processing.Xml, Output.Excel) reference no UI, OS, SSH, database or
logging implementation. The desktop and the worker build the same host, so behaviour is identical.

## Processing pipeline

`ProcessingPipeline.RunAsync` is the single code path:

1. Resolve the profile and mapper; acquire the cross-process lock (an in-process semaphore plus an exclusively
   opened lock file that the OS releases if the process dies).
2. Create and persist the job (`Discovered` → `Ready`).
3. `Validating`: existence, extension, size, stability window, format sniffing, SHA-256; then the file-level
   duplicate lookup (blocked unless forced; forced reruns are linked to the original job).
4. `Processing`: for each record, map → validate → duplicate-key check → write rows or a rejected line. Progress
   events are throttled. Only counts, per-code totals and a capped issue sample are kept.
5. `GeneratingOutput`: close sheets, write Summary, assemble the package, verify it, hash it, move it into place.
6. `Completed` / `CompletedWithWarnings`, then `Delivering` → `Delivered` when a provider is configured.
7. Always: write the JSON report and persist the job. Cancellation → `Cancelled`. Unusable input → `Quarantined`.

State transitions are enforced by `JobStateMachine`; illegal transitions throw.

## Streaming XML

`XmlReaderSettings`: DTD processing prohibited, `XmlResolver` null, character checking on, comments and processing
instructions ignored, entity expansion capped. The reader tracks the open-element path (namespace URI plus local
name); when it matches the profile's record path it reads exactly that subtree into an `XElement`, hands it to the
mapper, and drops it. A per-record size cap and a document size limit guard against pathological inputs.
Optional XSD validation attaches to the same pass. Malformed XML is a fatal error detected during the single pass;
the file is never read twice.

## Streaming XLSX

Worksheet XML is written with `XmlWriter` into deflate-compressed spool files on disk, one per sheet, and the
package is assembled with `ZipArchive` in create mode, one entry at a time. This keeps memory flat regardless of
row count. All text is written as inline strings, which Excel never evaluates, so values beginning with `=`, `+`,
`-` or `@` cannot become formulas. Numbers use invariant serialization; decimals keep full precision; dates are
serial numbers with number formats. Sheets split at 1,048,576 rows with repeated headers, an autofilter and a frozen
header row. The finished package is opened read-only and walked forward-only to verify it before the atomic rename.

Measured on a 200 MB, 299,008-record synthetic file: 20 s elapsed, 100 MB peak working set, 19 MB peak managed heap.

## Mapping profiles

Profiles are JSON validated against an embedded JSON Schema (`finxml profile schema`) plus semantic checks: namespace
prefixes must be declared, fields must reference existing tables and columns, a column can be bound once, date
columns need declared formats, validation rules must fit the column type, the safe identifier must be non-sensitive.
The compiled profile resolves qualified names and regular expressions once. Supported source paths are relative
element chains with an optional trailing attribute (`a/b/@c`), the record's own attributes (`@x`) or its text (`.`).
Transforms: upper, lower, trim, whitespace normalisation, constant, concatenation. There is deliberately no XPath,
scripting or expression language; a custom `IRecordMapperFactory` handles anything beyond that.

## Persistence

SQLite in WAL mode with a busy timeout, idempotent versioned migrations. Tables: `jobs`, `job_transitions`,
`job_issues`, `delivery_attempts`, `scheduled_runs` (ledger keyed by schedule id and Eastern date), `quarantine`,
`settings`, `schema_version`. Indexes on source hash, created time, status and scheduled date. Source records are
never stored. Record duplicate keys for the current job are SHA-256 digests in a scratch SQLite file that is deleted
when the job ends.

## Scheduling

`DailyScheduleService` computes occurrences in `America/New_York` with NodaTime (lenient resolution of skipped and
ambiguous local times during DST changes). `ScheduledRunCoordinator.RunDueAsync` checks the catch-up window and the
ledger, claims the business date before doing any work, acquires input (SFTP if configured, otherwise the input
folder), runs the pipeline and records the outcome. A `no-input` outcome may be retried within the window; any other
outcome is final for that date. The macOS LaunchAgent runs `finxml schedule run-due --quiet` at load and on a fixed
interval; because the worker is idempotent, no daemon state is needed and sleep or reboot simply delays the run.

## Security model

- XML: no DTD, no external entities, size and record caps, format sniffing before parsing.
- Paths: only file-name components of external names are used; every delete or move inside the data folder is
  resolved and checked against its root; external inputs are never moved or deleted.
- Secrets: Keychain (macOS) or DPAPI (Windows) via `ISecretStore`; never in settings, logs, reports, bundles or
  command lines. The logging pipeline redacts secret-like properties, private-key blocks, URL credentials and tokens.
- Data: columns carry a sensitivity classification (`none`, `sensitive`, `restricted`) that drives masking in
  rejection output and reports; conversion errors describe value shape only.
- SFTP: host-key algorithm and SHA-256 fingerprint are mandatory; no trust on first use; downloads are size-checked
  and hashed; authentication failures are not retried indefinitely.
- Supply chain: central package versions with lock files, locked restore in CI, Dependabot, first-party Actions pinned
  to commit SHAs, vulnerable-package scan, SBOM on releases.

## Extension points

| Contract | Purpose | Shipped |
| --- | --- | --- |
| `IRecordMapperFactory` | Turn a record into rows; selected by the profile's `mapperType` | `profile` |
| `IInputAcquirer` | Find and fetch input files | `local`, `sftp` |
| `IOutputDelivery` | Hand a finished workbook to a destination; derive from `InternalSystemDeliveryBase` and inherit the contract tests | `local-folder` |
| `IWorkbookWriter` | Another output format | streaming XLSX |
| `ISecretStore` | Another secure store | Keychain, DPAPI, in-memory (tests) |
| `IBackgroundAgentManager` | Another scheduler integration | LaunchAgent, no-op |

## Testing

Unit tests per layer (state machine, profile schema and semantics, every cell conversion and transform, validation
rules, sheet naming and splitting, formula-literal defence, redaction, DST and catch-up logic, lock, delivery contract),
integration tests (end-to-end demo run with workbook cell checks, duplicate and forced rerun, malformed input
quarantine, cancellation cleanup, lock contention, delivery, CLI exit codes, bounded memory on a generated dataset) and
Avalonia headless UI tests. CI runs on Windows and macOS; a manual workflow runs the 200 MB benchmark on a Mac runner.

## Packaging

`scripts/macos/build-app.sh` publishes self-contained desktop and worker binaries into one `.app` bundle with the
worker at `Contents/MacOS/finxml`, generates the `.icns`, verifies the bundle and runs the worker self-test.
`sign-and-notarize.sh` signs nested libraries and executables inside-out with the hardened runtime, creates a DMG,
notarizes, staples and verifies. The release workflow does this for Apple Silicon and Intel and publishes the DMGs,
checksums and SBOMs; the artifact workflow produces clearly labelled unsigned test builds without any credentials.
