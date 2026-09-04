# Security

The application handles financial data locally on a user's Mac. This document describes the threat model, the
controls in place, and the data-handling model.

## Threat model and controls

| Threat | Control |
| --- | --- |
| XML external entities, entity expansion, DTD tricks | `XmlReaderSettings`: `DtdProcessing.Prohibit`, `XmlResolver = null`, `CheckCharacters`, entity character cap; DOCTYPE is a fatal, quarantining error (`XML-003`). XSD loading also uses a null resolver. Tests: `StreamingXmlRecordReaderTests` |
| Oversized inputs / runaway records | Configurable maximum input size (default 1 GB, `FILE-005`), per-record fragment cap (`XML-005`), bounded-memory streaming end to end |
| Compressed, encrypted or binary files disguised as XML | Format sniffing rejects gzip/zip/xz/bzip2/RAR/PGP/binary content before parsing (`FILE-008`) |
| Path traversal and malicious file names | Only the file-name component of remote or user-supplied names is used; names are sanitised (reserved names, control characters, separators). Every delete/move inside the data folder goes through `IAppPaths.ResolveInside`, which rejects paths outside the expected root. External (user-selected) inputs are never moved or deleted |
| Formula injection in Excel | All text is written as inline strings; no `<f>` element is ever emitted. Text starting with `=`, `+`, `-`, `@` stays literal. Tests: `Formula_like_text_stays_literal` |
| Credential leakage | Secrets live only in the macOS Keychain (DPAPI on Windows dev machines); never in settings files, logs, reports, diagnostic bundles or command lines (`sftp set-secret` reads standard input). Structured logging runs through a redaction enricher (secret-like property names, private-key blocks, URL credentials, bearer tokens, `key=value` secrets). Tests: `SecurityAndAgentTests` |
| Sensitive financial values in logs/reports | Columns carry a sensitivity classification. Conversion errors describe the *shape* of a value, never the value. Rejection output includes only fields the profile marks safe; `sensitive` values are masked, `restricted` values never leave the workbook. Reports contain counts and sanitized issues |
| Tampered profiles | Profiles are validated against an embedded JSON Schema plus semantic rules; the SHA-256 of the profile text is recorded on every job and in the Summary sheet |
| Malicious or spoofed SFTP server | Host-key algorithm and SHA-256 fingerprint are mandatory and pinned; there is no trust-on-first-use. Authentication and host-key failures are not retried indefinitely. Downloads are size-verified and hashed locally |
| Duplicate or replayed inputs | SHA-256 file identity blocks reprocessing unless explicitly forced (recorded as a linked rerun); record-level duplicate keys are profile-defined |
| Concurrent runs (desktop + worker) | In-process semaphore plus OS-level exclusive lock file; SQLite in WAL mode with a busy timeout |
| Tampered history rows | Quarantine deletes re-validate the recorded path against the quarantine root before touching the filesystem |
| Supply chain | Central package versions, lock files with `--locked-mode` restore in CI, Dependabot for NuGet and Actions, first-party Actions pinned to commit SHAs, vulnerable-package scan in CI, CycloneDX SBOM attached to releases |

SHA-256 is used for identity and integrity. It is not encryption; the data folder is protected by macOS file
permissions (the root is created `0700` on non-Windows systems) and FileVault if enabled.

## Sensitive settings

| Setting | Storage |
| --- | --- |
| SFTP password, private-key passphrase | `ISecretStore` (Keychain / DPAPI) under service `com.example.finxmlprocessor` |
| SFTP host, port, user, key path, fingerprint | `settings/appsettings.json` (non-secret, redacted in bundles) |
| Everything else | `settings/appsettings.json` or the SQLite `settings` table |

Sample private keys are never committed; `.gitignore` excludes `*.pem`, `*.key`, `*.p12`, `*.pfx`, `*.env`.

## Data handling model

- Input XML is read in place and never modified. Managed inputs that prove unusable are moved to `quarantine/`;
  external inputs are only recorded.
- The database stores job metadata, counts, transitions, sanitized issues, artifact paths and hashes, delivery
  attempts and the scheduled-run ledger. It never stores source records.
- Reports and logs never contain raw financial values by default.
- Diagnostic bundles exclude input XML, workbooks, the database, keys and secrets.
- Retention is opt-in per category; nothing is deleted automatically until enabled.

## Release trust

Releases are built on GitHub-hosted macOS runners, signed with a Developer ID Application certificate using the
hardened runtime and a secure timestamp, notarized with Apple, stapled, and published with SHA-256 checksums and an
SBOM. The certificate and API key are imported into a temporary keychain that is deleted in an always-run cleanup step.
Unsigned artifacts are clearly labelled test builds.

## Reporting a vulnerability

See `SECURITY.md` at the repository root.
