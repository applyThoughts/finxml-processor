<p align="center"><img src="scripts/branding/logo.svg" alt="FinXml Processor logo" width="128" height="128"></p>

# FinXml Processor

A macOS desktop utility (with a headless worker) that turns large daily fintech XML files into clean Excel workbooks
without loading the document or the workbook into memory, and that can run the whole job automatically at 7:00 PM
Eastern Time.

> **Status: initial repository, synthetic data only.** The processing engine, streaming XML→XLSX path, history,
> quarantine, reports, scheduling and SFTP acquisition are implemented and tested. The mapping profile shipped in
> this repository is a **synthetic demonstration** (`demo-fintech-v1`); the real XML layout, field mapping,
> business validation rules, SFTP endpoint and internal-system delivery are external inputs that arrive as
> configuration, a profile and an adapter (see [Deferred inputs](#deferred-inputs)).

## What it does

- Streams a 120–200 MB (up to a configurable 1 GB) XML file record by record with a hardened `XmlReader`
  (DTD prohibited, no external resolver), maps each record through a declarative **mapping profile** and writes
  rows straight into an XLSX package. Measured: a 200 MB / 299,008-record synthetic file completes in about
  20 seconds with a 100 MB peak working set on a Windows development machine (see [benchmarks](benchmarks/README.md)).
- Validates the file (extension, stability, size, format sniffing, SHA-256), detects **duplicate files** by hash
  and **duplicate records** by a profile-declared composite key, and rejects invalid records with stable error codes
  into a `Rejected Records` sheet that never contains restricted values.
- Records every job, state transition, sanitized issue, output artifact and delivery attempt in a local SQLite
  history, writes a JSON report per run, and quarantines unusable inputs.
- Runs automatically through a per-user macOS **LaunchAgent** that invokes the idempotent worker; missed runs are
  caught up after wake or reboot, once per Eastern business date.
- Optionally downloads the daily file over **SFTP** with SSH key authentication and a pinned host-key fingerprint;
  secrets live in the macOS Keychain (DPAPI on Windows development machines), never in source or settings files.
- Delivers completed workbooks to a local folder today; the internal-system delivery is a documented adapter
  contract with contract tests, not a fake integration.

## Screens

Dashboard (latest result, next schedule, Run Now) · Process File (drag/drop, validation preview, progress, cancel) ·
History (sortable jobs, reports, rerun) · Quarantine (reveal/restore/delete) · Profiles (validation status,
import/export, set active) · Settings (folders, schedule and LaunchAgent, SFTP, delivery, retention, diagnostics).

Screenshots are produced by the macOS artifact workflow once a Mac runner build exists; the Windows development
build renders the same Avalonia UI.

## Supported platforms

| Role | Platform |
| --- | --- |
| End users | macOS 12+ on Apple Silicon (primary) and Intel, self-contained `.app` (no .NET install required) |
| Development | Windows 11 (primary), macOS; .NET SDK 10.0.200+ |
| Packaging and signing | GitHub-hosted macOS runners |

## Quick start (development, Windows or macOS)

```bash
git clone <your-fork-url> finxml-processor
cd finxml-processor
dotnet build FinXmlProcessor.sln -c Release
dotnet test FinXmlProcessor.sln -c Release
```

Run the worker against the bundled demo file (data folder isolated by `FINXML_HOME`):

```bash
FINXML_HOME=$PWD/.dev-home dotnet run --project src/FinXmlProcessor.Worker -c Release -- self-test
```

Run the desktop app:

```bash
FINXML_HOME=$PWD/.dev-home dotnet run --project src/FinXmlProcessor.Desktop -c Release
```

On Windows, `scripts/windows/dev.ps1 build|test|run|cli|gen|bench|format` wraps the same commands.

## Worker / CLI

```text
finxml process --input <path> [--profile <id|path>] [--output <dir>] [--force]
finxml schedule run-due | run-now | status | agent status|install|uninstall|render
finxml profile validate <path> | list | import <path> | schema
finxml sftp test | set-secret <name>   (secret read from standard input) | delete-secret <name>
finxml diagnostics [--bundle <zip>]
finxml self-test
finxml retention
finxml benchmark --input <path> [--result <json>]
Global: --json, --quiet, --set Section:Key=value
```

Exit codes: 0 ok · 1 unexpected · 2 configuration · 3 invalid input · 4 duplicate blocked · 5 processing failed ·
6 output failed · 7 delivery failed · 8 cancelled · 9 another job holds the lock.

## Repository layout

```text
src/        Domain, Application (contracts, profiles, pipeline), Processing.Xml, Output.Excel,
            Infrastructure (SQLite, locking, quarantine, reports, delivery, SFTP, secrets, scheduling,
            LaunchAgent, logging), Worker (CLI), Desktop (Avalonia)
tests/      Unit tests per layer, integration tests (end-to-end, CLI, bounded memory), headless UI tests
tools/      Streaming synthetic data generator
samples/    demo-fintech-v1 profile and a 250-record demo input
scripts/    macOS bundle/sign/notarize scripts, Windows dev helper
docs/       architecture, mapping profiles, macOS operations, security, release
```

## Documentation

- [Architecture](docs/architecture.md) · [Mapping profiles](docs/mapping-profiles.md) ·
  [macOS operations](docs/operations-macos.md) · [Security](docs/security.md) · [Release process](docs/release.md)
- [Benchmarks](benchmarks/README.md) · [Contributing](CONTRIBUTING.md) · [Security policy](SECURITY.md)

## Deferred inputs

These are not coding blockers; each is represented by configuration, a profile, a credential or an adapter:
the real XML/XSD and namespaces; the real field mapping and workbook layout; business validation and duplicate
rules; the SFTP host, path, host-key fingerprint and credentials; the internal-system protocol; the final product
name, bundle identifier, icon and branding; Apple Developer credentials; data-retention policy.

The synthetic profile is flagged as such in the UI, in every report and on the workbook's Summary sheet so demo
behaviour is never mistaken for approved financial processing rules.

## License

Choose a license before publishing this repository. Third-party packages are Apache 2.0/MIT
(FluentAssertions is pinned to the Apache 2.0 7.x line).
