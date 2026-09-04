# Operating FinXml Processor on macOS

## Install

1. Download the DMG for your Mac from the GitHub Releases page (Apple Silicon: `osx-arm64`; Intel: `osx-x64`).
   Verify the checksum: `shasum -a 256 -c FinXmlProcessor-<version>-<rid>.dmg.sha256`.
2. Open the DMG and drag **FinXml Processor** into **Applications**. Automation requires the app to live at
   `/Applications/FinXml Processor.app`; the Settings page checks this before installing the agent.
3. Launch the app. On first launch it creates its data folders and database and installs the demo profile.

Unsigned test builds (from the "macOS artifacts" workflow) are blocked by Gatekeeper. Testers only: right-click →
Open once, or `xattr -dr com.apple.quarantine "/Applications/FinXml Processor.app"`. Never ask production users to
disable Gatekeeper globally.

## Folders

```text
~/Library/Application Support/FinXmlProcessor/
  settings/     appsettings.json (non-secret settings written by the Settings page)
  profiles/     installed mapping profiles
  database/     history.sqlite (jobs, transitions, issues, deliveries, scheduled-run ledger, quarantine)
  staging/      SFTP downloads (.part then final) and per-job duplicate-key scratch files
  quarantine/   unusable inputs moved from managed folders
  reports/      report_<date>_<job>.json, one per run
  logs/         finxml-<date>.json (structured, redacted), launchagent.*.log
  input/        default input folder (Run Now / schedule)
  output/       default output folder
```

Input and output folders can be changed in Settings. Set `FINXML_HOME` to relocate the whole data folder
(also used by tests and CI).

## Running a file manually

**Process File** page: drag an XML file (or Browse), pick the profile and output folder, optionally **Validate** for a
quick preview of the first 200 records, then **Start**. Progress shows bytes, records seen/accepted/rejected/duplicates;
**Cancel** stops cooperatively and removes staging output. The original input is never modified or deleted.

Outputs are named `<profile>_<businessDate>_<jobId8>.xlsx` with sheets `Summary`, one per profile table, and
`Rejected Records` when any record was rejected. The business date is the Eastern calendar date of the run (or the
scheduled date for scheduled runs).

## Scheduled processing (7:00 PM Eastern)

Enable in **Settings → Daily schedule**, adjust the time and catch-up window if needed, then **Install or update
agent**. This writes `~/Library/LaunchAgents/com.example.finxmlprocessor.worker.plist` and loads it with
`launchctl bootstrap gui/<uid>`. The agent runs `finxml schedule run-due --quiet` at load and every 5 minutes.
The worker decides whether the day's run is due; if it is, it claims the Eastern business date in the ledger, acquires
the input (SFTP when configured, otherwise the newest unprocessed file in the input folder), processes it and records
the outcome. `finxml schedule status` and the Dashboard show the next occurrence in Eastern and local time.

### What happens when the Mac was asleep or off

| Situation | Behaviour |
| --- | --- |
| Mac asleep at 7:00 PM, wakes later | launchd runs the agent on wake; the missed run is caught up if still inside the catch-up window (default 20 hours) |
| Mac powered off at 7:00 PM | Nothing can run while powered off; the run is caught up after boot and login, within the window |
| No input file yet at 7:00 PM | The ledger records `no-input`; the worker keeps checking every interval until the window closes |
| Run already completed for the date | Further invocations are no-ops (`already recorded`) |
| Desktop app processing at the same time | The worker sees the processing lock and skips; the date remains claimable |

Limitations: a per-user LaunchAgent only runs while your user session exists (logged in, possibly locked). Guaranteed
execution without a logged-in user needs a privileged LaunchDaemon or a server deployment, which is outside this
scope. Calendar/holiday exclusions are future profile settings.

## SFTP

Settings → SFTP: host, port, user, key file (or password), remote directory and pattern, plus the **required**
expected host key algorithm and SHA-256 fingerprint (`ssh-keyscan -t ed25519 host | ssh-keygen -lf -` on a trusted
machine gives the fingerprint). Store the key passphrase or password with **Store in secure store** (macOS Keychain);
the value is never displayed or written to settings. **Test connection** reports a sanitized result. Downloads go to
`staging/` as `.part`, are size-checked, hashed and renamed; the remote file is never deleted or moved unless
`ArchiveRemoteAfterDownload` is enabled with an archive directory.

## Logs, reports and diagnostics

- Reports: JSON per run under `reports/`, rendered in History. Counts and sanitized issue codes only.
- Logs: compact JSON under `logs/`, 30 daily files by default, secret-like values redacted.
- Settings → Diagnostics lists environment facts; **Export diagnostic bundle** creates a ZIP with sanitized
  diagnostics, redacted settings, recent logs, reports and profiles. It never contains input XML, workbooks, the
  database, keys or secrets. The CLI equivalent is `finxml diagnostics --bundle <file>`.

## Recovery

- **Quarantine** page: reveal, restore to the input folder, or delete quarantined copies (confirmation required).
- **History → Rerun**: forced rerun of the same content, linked to the original job.
- A stale lock is impossible to leave behind: the OS releases the exclusive lock file when a process dies; the
  Settings diagnostics show the holder if one exists.
- Corrupted settings JSON falls back to defaults with a warning in the log; the Settings page rewrites it on save.

## Retention and cleanup

All retention is disabled by default (nothing is deleted automatically). Enable per category in Settings
(logs, reports, quarantine, history). Staging scratch files older than two days are cleaned. Cleanup only deletes
files inside the application's own folders that match known patterns; configured paths are resolved and checked
before any delete.

## Uninstall

1. Settings → **Remove agent** (or `finxml schedule agent uninstall`).
2. Delete `/Applications/FinXml Processor.app`.
3. Optionally delete `~/Library/Application Support/FinXmlProcessor` and the Keychain items for service
   `com.example.finxmlprocessor`.
