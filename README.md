<p align="center"><img src="scripts/branding/logo.svg" alt="FinXml Processor logo" width="128" height="128"></p>

# FinXml Processor

A desktop application and command-line worker that turns large daily XML files into clean Excel workbooks. It streams
records instead of loading the file, keeps a full history of every run, quarantines files it cannot process, and can
run unattended every day at a fixed Eastern time, optionally pulling the file from an SFTP server first.

- **Fast and small**: a 200 MB file with 299,008 records processes in about 20 seconds using roughly 100 MB of RAM.
- **Configurable**: the XML layout, columns, parsing, validation and duplicate rules live in a JSON mapping profile.
- **Safe by default**: hardened XML parsing, no formula injection, secrets in the OS secure store, sanitized logs.
- **Cross-platform**: built for macOS (Apple Silicon and Intel), runs on Windows and Linux for development and automation.

Documentation is also available inside the application (Documentation page):
[Feature documentation](docs/feature-documentation.md) · [Technical documentation](docs/technical-documentation.md).

## Contents

1. [Running the application](#running-the-application)
2. [Processing a file](#processing-a-file)
3. [Creating a mapping profile](#creating-a-mapping-profile)
4. [Generating demo files](#generating-demo-files)
5. [Setting up SFTP](#setting-up-sftp)
6. [Automatic daily runs](#automatic-daily-runs)
7. [Output and export options](#output-and-export-options)
8. [Command-line reference](#command-line-reference)
9. [Building, testing and packaging](#building-testing-and-packaging)
10. [More documentation](#more-documentation)

## Running the application

### macOS (end users)

Download the DMG for your Mac from the Releases page (`osx-arm64` for Apple Silicon, `osx-x64` for Intel), verify the
checksum, drag **FinXml Processor** into Applications and launch it. First launch creates the data folder under
`~/Library/Application Support/FinXmlProcessor` and installs the demo profile.

```bash
shasum -a 256 -c FinXmlProcessor-<version>-osx-arm64.dmg.sha256
```

### From source (Windows, macOS, Linux)

Requires the .NET SDK 10.0.200 or later.

```bash
git clone <repository-url>
cd finxml-processor
dotnet build FinXmlProcessor.sln -c Release
dotnet run --project src/FinXmlProcessor.Desktop -c Release
```

Set `FINXML_HOME` to keep data in a folder of your choice (the default is `%LocalAppData%\FinXmlProcessor` on
Windows, `~/Library/Application Support/FinXmlProcessor` on macOS, `~/.local/share/FinXmlProcessor` on Linux).
On Windows, `scripts/windows/dev.ps1 run` launches the app against a local `.dev-home` folder.

Try the bundled demo end to end from the command line:

```bash
dotnet run --project src/FinXmlProcessor.Worker -c Release -- self-test
```

## Processing a file

1. Open **Process File**, drag an XML file onto the page or click Browse.
2. Choose the mapping profile and the output folder.
3. Click **Validate (preview)** to check the file and see what the first 200 records would produce.
4. Click **Start processing**. Watch progress, or **Cancel** at any time; cancelled runs leave no output behind.
5. When finished, open the workbook or the report from the result panel, the Dashboard, or the History page.

The workbook contains a `Summary` sheet (source file, hashes, profile, counts, timings), one sheet per output table
(split automatically at Excel's row limit) and a `Rejected Records` sheet listing each rejected record with its
error codes. Identical content is not processed twice unless you tick **Force**.

The same run from the command line:

```bash
finxml process --input /path/to/file.xml --profile demo-fintech-v1 --output /path/to/output
```

## Creating a mapping profile

Profiles are JSON files. Start from [samples/profiles/demo-fintech-v1.json](samples/profiles/demo-fintech-v1.json):

```json
{
  "schemaVersion": 1,
  "id": "my-feed-v1",
  "displayName": "My daily feed",
  "version": "1.0.0",
  "namespaces": { "f": "urn:my-company:feed:v1" },
  "recordPath": ["f:Feed", "f:Records", "f:Record"],
  "safeIdentifierField": "recordId",
  "duplicateKeyFields": ["recordId"],
  "tables": [
    {
      "id": "records",
      "sheetName": "Records",
      "columns": [
        { "id": "recordId", "heading": "Record ID", "cellType": "text", "width": 24 },
        { "id": "amount", "heading": "Amount", "cellType": "decimal", "numberFormat": "#,##0.00", "sensitivity": "restricted" },
        { "id": "postedAt", "heading": "Posted (UTC)", "cellType": "dateTime" }
      ]
    }
  ],
  "fields": [
    { "id": "recordId", "source": "f:Id", "table": "records", "column": "recordId", "required": true },
    { "id": "amount", "source": "f:Amount", "table": "records", "column": "amount", "required": true },
    { "id": "postedAt", "source": "f:PostedAt", "table": "records", "column": "postedAt",
      "parse": { "dateFormats": ["yyyy-MM-dd'T'HH:mm:ssK"] } }
  ]
}
```

- `recordPath` is the element path from the document root to the repeating record; the first entry must be the root.
- `source` is relative to the record: `f:Amount`, `f:Header/f:Id`, `f:Amount/@currency`, `@id` or `.`.
- Column `cellType`: `text`, `integer`, `decimal`, `date`, `dateTime`, `boolean`. Identifiers should stay `text`.
- `sensitivity` (`none`, `sensitive`, `restricted`) controls what may appear in rejection output and reports.
- Optional per field: `required`, `default`, `trim`, `transforms` (upper, lower, trim, normalizeWhitespace, constant,
  concat), `parse` (date formats, culture, boolean words), `validation` (length, pattern, allowed values, ranges).

Validate and install it:

```bash
finxml profile validate my-feed-v1.json
finxml profile import my-feed-v1.json
```

Then make it active on the **Profiles** page (or set `Processing:ActiveProfileId`). The full reference is in
[docs/mapping-profiles.md](docs/mapping-profiles.md). The demo profile is synthetic; its rules are placeholders.

## Generating demo files

The repository includes a streaming synthetic data generator that produces files matching the demo profile at any
size, optionally with bad records:

```bash
dotnet run --project tools/FinXmlProcessor.TestDataGenerator -c Release -- --output demo-200mb.xml --approx-size 200MB
```

Useful options: `--records N` for an exact count, `--missing-rate`, `--invalid-date-rate`, `--invalid-decimal-rate`,
`--duplicate-rate`, `--special-rate` (Unicode and XML special characters), `--long-field-rate`, `--invalid-status-rate`,
`--default-namespace` (same data, different XML serialisation), `--truncate` (malformed document), `--doctype`
(rejected by design), `--compact` (no indentation), `--seed N` for reproducibility. A 250-record sample is committed at
`samples/input/demo-transactions.xml`.

Measure a run while sampling memory:

```bash
finxml benchmark --input demo-200mb.xml --output ./out --result result.json
```

## Setting up SFTP

1. On a trusted machine, capture the server's host key fingerprint:
   ```bash
   ssh-keyscan -t ed25519 sftp.example.net | ssh-keygen -lf -
   ```
2. In **Settings → SFTP acquisition**, enter host, port, user name, authentication method (`key` preferred),
   the private key file, remote directory and file pattern, and the host key algorithm and `SHA256:…` fingerprint.
   Both are required; the application never trusts a server on first contact.
3. Enter the key passphrase or password in the secret box and click **Store in secure store**. It is kept in the
   macOS Keychain (Windows DPAPI on development machines), never in settings files, and never shown again.
4. Click **Test connection**; a sanitized result reports success or the reason for failure.
5. Tick **Enable SFTP download before processing** and save.

Run Now and the scheduler now download the newest matching file that has not been processed to a staging area,
verify its size and hash, and process it. The remote file is left untouched unless remote archiving is enabled.
From the command line, `finxml sftp set-secret sftp.key-passphrase` reads the secret from standard input and
`finxml sftp test` tests the connection.

## Automatic daily runs

1. **Settings → Daily schedule**: enable, set the Eastern time (default 19:00) and the catch-up window.
2. On macOS click **Install or update agent**. This installs a per-user LaunchAgent that invokes the worker at login
   and every five minutes. The worker decides whether the run is due, runs it once per business date, and catches up
   a missed run when the Mac wakes or boots within the window. The Dashboard shows the next occurrence in Eastern and
   local time; `finxml schedule status` shows the same.
3. On Windows or Linux, schedule the equivalent command with Task Scheduler or cron every few minutes:
   ```bash
   finxml schedule run-due --quiet
   ```

The Mac must be powered on with a logged-in user session for the agent to run. See
[docs/operations-macos.md](docs/operations-macos.md) for details and limitations.

## Output and export options

- **Output folder** (Settings → Folders, or per run on Process File): where workbooks are written, named
  `<profile>_<business date>_<job id>.xlsx`.
- **Delivery → none**: workbooks stay in the output folder.
- **Delivery → local-folder**: each finished workbook is also copied to a folder of your choice, through a temporary
  file and an atomic rename, with a verified hash. The **collision policy** decides what happens when a file with the
  same name exists: `version` (`name (2).xlsx`, default), `fail`, or `overwrite`.
- **Reports**: a JSON report per run under `reports/` in the data folder, rendered on the History page.
- **Other destinations**: implement a delivery adapter (`IOutputDelivery`, base class `InternalSystemDeliveryBase`)
  for an API, queue or database loader; contract tests are provided. See the technical documentation.

## Command-line reference

```text
finxml process --input <path> [--profile <id|path>] [--output <dir>] [--force]
finxml schedule run-due | run-now | status | agent status|install|uninstall|render
finxml profile validate <path> | list | import <path> | schema
finxml sftp test | set-secret <name> | delete-secret <name>
finxml diagnostics [--bundle <zip>]
finxml self-test
finxml retention
finxml benchmark --input <path> [--profile <id>] [--output <dir>] [--result <json>]
Global options: --json  --quiet  --set Section:Key=value
```

Exit codes: 0 ok · 1 unexpected · 2 configuration · 3 invalid input · 4 duplicate blocked · 5 processing failed ·
6 output failed · 7 delivery failed · 8 cancelled · 9 another job holds the lock.

## Building, testing and packaging

```bash
dotnet restore FinXmlProcessor.sln --locked-mode
dotnet build FinXmlProcessor.sln -c Release
dotnet test FinXmlProcessor.sln -c Release
dotnet format FinXmlProcessor.sln --verify-no-changes
```

- `ci.yml` builds and tests on Windows and macOS for every pull request.
- `macos-artifacts.yml` produces unsigned `.app` test builds for Apple Silicon and Intel.
- `release.yml` signs, notarizes and publishes DMGs when a `v*` tag is pushed (requires Apple credentials as
  repository secrets; see [docs/release.md](docs/release.md)).

Repository layout: `src/` (Domain, Application, Processing.Xml, Output.Excel, Infrastructure, Worker, Desktop),
`tests/`, `tools/` (data generator), `samples/`, `scripts/`, `docs/`.

## More documentation

- [Feature documentation](docs/feature-documentation.md) and [Technical documentation](docs/technical-documentation.md)
- [Architecture](docs/architecture.md) · [Mapping profiles](docs/mapping-profiles.md) ·
  [macOS operations](docs/operations-macos.md) · [Security](docs/security.md) · [Release process](docs/release.md)
- [Benchmarks](benchmarks/README.md) · [Contributing](CONTRIBUTING.md) · [Security policy](SECURITY.md)
