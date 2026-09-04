# Security policy

## Reporting a vulnerability

Please do not open a public issue for security problems. Use GitHub's **private vulnerability reporting** on this
repository ("Security" tab → "Report a vulnerability"). Include the version, platform, steps to reproduce and any
sanitized logs. Do not include real financial data, credentials or private keys in a report.

You can expect an acknowledgement within five working days and a fix or mitigation plan for confirmed issues.

## Supported versions

Only the latest released version receives security fixes.

## Data-handling model (summary)

- Input XML is read in place and never modified; unusable managed inputs are quarantined, external ones only recorded.
- Secrets (SFTP password, key passphrase) are stored in the macOS Keychain (DPAPI on Windows development machines)
  and are never written to settings, logs, reports, bundles or command lines.
- Logs, reports and diagnostic bundles are sanitized and redacted; they never contain raw financial values, the
  database, workbooks or input files.
- Outputs are written locally; delivery to other systems happens only through an explicitly configured provider.
- SHA-256 is used for file identity and integrity checks; it is not encryption. Protect the data folder with macOS
  permissions and FileVault.

See `docs/security.md` for the full threat model and controls.
