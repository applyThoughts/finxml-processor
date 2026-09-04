# Feature documentation

FinXml Processor turns large daily XML files into clean Excel workbooks. It runs as a desktop application and as a
command-line worker, keeps a history of every run, and can run unattended every day at a fixed Eastern time.

## What a run does

1. **Validates the file**: extension, size limit, that the file is no longer being written, and that it really is
   plain XML (compressed, encrypted or binary files are rejected with a clear message). A SHA-256 hash identifies it.
2. **Checks for duplicates**: identical content processed before is blocked unless you choose to force a rerun.
3. **Streams the records** through the active mapping profile: each XML record becomes one typed row per output
   table. Required fields, number and date parsing, length, pattern, allowed values and ranges are checked.
   Records that fail are rejected with a stable error code; records that repeat the profile's duplicate key are
   rejected as duplicates.
4. **Writes the workbook** as it goes: a `Summary` sheet, one sheet per table (split into `Name (2)`, `Name (3)`
   when Excel's row limit is reached) and a `Rejected Records` sheet when anything was rejected.
5. **Records the result**: the job, its status transitions, counts and sanitized issues go to the local history
   database, and a JSON report is written for the run.
6. **Delivers** the workbook to the configured destination, if one is enabled.

Nothing is ever loaded whole into memory: a 200 MB file processes in about 20 seconds using around 100 MB of RAM.

## Screens

### Dashboard
The latest result with links to its workbook and report, the next scheduled run in Eastern and local time, how many
files are waiting in the input folder, the active profile, and a **Run Now** button that acquires and processes the
newest unprocessed file. A banner reminds you when the active profile is the synthetic demonstration profile.

### Process File
Drag an XML file onto the page or browse to it, pick a profile and an output folder, optionally tick **Force**.
**Validate (preview)** checks the file and maps the first 200 records without writing anything, so you can see
what would be rejected. **Start processing** runs the job with a progress bar (bytes, records seen, accepted,
rejected, duplicates); **Cancel** stops cooperatively and removes any partial output. The original input file is
never modified or deleted.

### History
Every job, newest first, with status, counts and duration. Filter by file name or status. Select a job to read its
report; open its workbook or report file; **Rerun** processes a file again with duplicate protection bypassed and
links the new job to the original.

### Quarantine
Inputs that could not be processed: unsupported formats, malformed XML, schema violations. Files from the managed
input folder are moved here; files you selected elsewhere are only recorded, never moved. You can reveal, restore to
the input folder, or delete a quarantined copy (with confirmation).

### Profiles
The installed mapping profiles with their validation status, version and hash. Import a profile JSON file (it is
validated against the built-in schema first), export one, or make one active. Invalid profiles show their errors and
cannot be activated.

### Documentation
This guide and the technical guide, rendered inside the application.

### Settings
- **Folders**: input and output folders, the input file pattern, the maximum input size.
- **Daily schedule**: enable scheduled processing, the Eastern time of day, the catch-up window, and the macOS
  background agent (install, update, remove, status).
- **SFTP acquisition**: host, port, user, key file or password, remote directory and pattern, and the required
  server host-key algorithm and fingerprint. Secrets are stored in the system secure store and never displayed.
- **Delivery**: where completed workbooks go and what happens when a file with the same name already exists.
- **Retention**: optional automatic cleanup of logs, reports, quarantined copies and history. Everything is kept
  until you enable a policy.
- **Appearance and diagnostics**: light, dark or system theme; environment facts; export a sanitized diagnostic
  bundle; open the data folder.

## Mapping profiles

A mapping profile is a JSON document that describes where records are in the XML, which values become which columns,
how to parse and validate them, and which fields identify a duplicate record. Profiles are validated before use and
their hash is recorded on every job. The shipped `demo-fintech-v1` profile is a synthetic example whose rules are
placeholders; replace it with a profile for your own data. See the technical guide and `docs/mapping-profiles.md`
for the full format.

## Scheduled processing

Enable the schedule in Settings and install the background agent. On macOS a per-user LaunchAgent invokes the worker
at login and every few minutes; the worker decides whether today's 7:00 PM Eastern run (or the time you configured)
is due, claims the business date so it can run only once, acquires the input, processes it and records the outcome.
A run missed because the Mac was asleep or off is caught up when the Mac is next available, within the catch-up
window. On Windows or Linux, schedule `finxml schedule run-due` with the system scheduler every few minutes for the
same behaviour.

## Getting the daily file

- **Local folder**: drop the file into the input folder (or point the input folder at a network share). Run Now and
  the scheduler pick the newest file whose content has not been processed.
- **SFTP**: when enabled, the worker connects with SSH key (preferred) or password authentication, verifies the
  server's host key against the configured algorithm and SHA-256 fingerprint, downloads the newest matching file to a
  staging area, verifies its size and hash, and processes it. The remote file is never deleted or moved unless remote
  archiving is switched on.

## Output and delivery options

- Workbooks are named `<profile>_<business date>_<job id>.xlsx` and written to the output folder.
- **Delivery provider "none"**: the workbook stays in the output folder.
- **Delivery provider "local-folder"**: the workbook is copied to another folder (for example a shared drive an
  internal system watches). On a name collision the copy is versioned (`name (2).xlsx`), fails, or overwrites,
  according to the collision policy. The copy's hash is verified and recorded.
- Further destinations (an API, a queue, a database loader) are added as delivery adapters; see the technical guide.

## Reports, logs and diagnostics

Each run produces a JSON report with counts, timings, issue-code totals and a capped list of sanitized issues.
Logs are structured JSON with secret-like values redacted. The diagnostic bundle contains environment facts,
redacted settings, recent logs, reports and profiles; it never contains input XML, workbooks, the database,
keys or secrets. Error messages never echo field values; conversion problems describe the shape of a value instead.

## Command line

Everything the desktop does is available from the `finxml` worker, with documented exit codes, for automation:

```text
finxml process --input <path> [--profile <id|path>] [--output <dir>] [--force]
finxml generate --output <path> [--records N | --approx-size 200MB] [--clean] [--seed N] [anomaly-rate options]
finxml schedule run-due | run-now | status | agent status|install|uninstall|render
finxml profile validate <path> | list | import <path> | schema
finxml sftp test | set-secret <name> | delete-secret <name>
finxml diagnostics [--bundle <zip>]
finxml self-test | retention | benchmark --input <path> [--result <json>]
Global options: --json  --quiet  --set Section:Key=value
```
