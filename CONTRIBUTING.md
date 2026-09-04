# Contributing

## Development setup

- .NET SDK 10.0.200 or later (`global.json` rolls forward within the 10.0 feature band).
- Windows 11 or macOS. Everything except bundle signing runs on both; `.app` packaging needs macOS tooling
  (`sips`, `iconutil`, `ditto`) and is normally done by CI.

```bash
dotnet restore FinXmlProcessor.sln --locked-mode
dotnet build FinXmlProcessor.sln -c Release
dotnet test FinXmlProcessor.sln -c Release
dotnet format FinXmlProcessor.sln --verify-no-changes
```

Use `FINXML_HOME=<folder>` to keep development data out of your real application-support folder
(`scripts/windows/dev.ps1` sets it to `.dev-home`).

## Rules of the road

- Warnings are errors; analyzers and the formatter run in CI. Keep `.editorconfig` conventions.
- Package versions are central (`Directory.Packages.props`) with lock files; run `dotnet restore` after changing a
  version and commit the updated `packages.lock.json` files.
- The processing engine (`Domain`, `Application`, `Processing.Xml`, `Output.Excel`) must not reference Avalonia,
  macOS APIs, SSH.NET, SQLite or a concrete logger.
- Never introduce collections that grow with the record count. The integration test
  `Memory_stays_bounded_for_a_generated_dataset` will fail if you do.
- Never log or persist raw financial values or secrets; use the sensitivity classification and `Masking`.
- Every new issue code goes into `IssueCodes` and is never renumbered.
- Add or update tests with each change; contract tests exist for delivery providers (`DeliveryContractTests`).

## Pull requests

- Keep commits logically grouped and describe the user-visible behaviour change.
- Update the relevant document in `docs/` when behaviour or configuration changes.
- Do not commit generated inputs, outputs, `.dev-home`, keys or secrets (see `.gitignore`).
