# Release process

Development happens on Windows (or macOS); packaging, signing and notarization run on GitHub-hosted macOS runners.

## Workflows

| Workflow | Trigger | Output |
| --- | --- | --- |
| `ci.yml` | pull requests, pushes to `main` | Locked restore, format check, Release build, unit + integration + headless UI tests on Windows and macOS, vulnerable-package scan, test results and coverage artifacts |
| `macos-artifacts.yml` | manual, or pushes to `main` touching app code | Unsigned `.app` ZIPs for `osx-arm64` (Apple Silicon runner) and `osx-x64` (Intel runner), each with a SHA-256 file and an "unsigned test build" notice; a manual 200 MB benchmark job |
| `release.yml` | tag `vX.Y.Z` | Signed, notarized, stapled DMGs per architecture, checksums, CycloneDX SBOM, GitHub Release with notes |

First-party Actions are pinned to full commit SHAs and updated by Dependabot. CI runs with read-only permissions;
only the publish job of the release workflow has `contents: write`.

## Apple prerequisites

- Apple Developer Program membership.
- A **Developer ID Application** certificate exported as a password-protected `.p12`.
- An **App Store Connect API key** (Team key with Developer access) for `notarytool`: key id, issuer id, `.p8` file.

## Repository secrets (never in source)

| Secret | Content |
| --- | --- |
| `APPLE_DEVELOPER_ID_CERT_BASE64` | base64 of the Developer ID Application `.p12` |
| `APPLE_DEVELOPER_ID_CERT_PASSWORD` | the `.p12` password |
| `APPLE_TEAM_ID` | 10-character team identifier |
| `APPLE_API_KEY_ID` | App Store Connect API key id |
| `APPLE_API_ISSUER_ID` | App Store Connect issuer id |
| `APPLE_API_KEY_BASE64` | base64 of the `.p8` private key |

Create them under an environment named `release` so they can be protected with required reviewers. The release
workflow fails fast with a clear message if any secret is missing; the unsigned artifact workflow needs none of them.

## What the scripts do

- `scripts/macos/build-app.sh <rid> [out] [version]`: `dotnet publish` (self-contained, framework-dependent
  disabled) for the desktop app and the worker, assembles `FinXml Processor.app` with `Info.plist`, the worker
  executable (`Contents/MacOS/finxml`), sample profile and input, and an `.icns` generated from
  `src/FinXmlProcessor.Desktop/Assets/icon-1024.png`; runs `verify-app.sh`; zips with `ditto`.
- `scripts/macos/verify-app.sh <app> [--signed]`: checks the bundle layout, executable bits, `Info.plist`, runs the
  worker self-test end to end, renders the agent definition; with `--signed` also `codesign --verify --strict`,
  the hardened-runtime flag and `spctl --assess`.
- `scripts/macos/sign-and-notarize.sh`: signs native libraries and Mach-O executables inside-out, then the bundle,
  with `--options runtime --timestamp` and the entitlements needed by .NET (JIT, unsigned executable memory,
  library validation disabled); creates a DMG, signs it, submits with `notarytool --wait`, fetches the log on failure,
  staples and validates. `codesign --deep` is not used as a substitute for correct nested signing.

## Cutting a release

1. Update `VersionPrefix` in `Directory.Build.props` (and the product name/bundle identifier if they are still the
   placeholders; see `AppInfo`, `Info.plist.template`, `LaunchAgentManager.Label`).
2. Merge to `main`, confirm CI is green, optionally run the manual macOS artifact workflow and the 200 MB benchmark.
3. Tag: `git tag v1.0.0 && git push origin v1.0.0`.
4. The release workflow publishes `FinXmlProcessor-1.0.0-osx-arm64.dmg`, `…-osx-x64.dmg`, their `.sha256` files
   and the SBOMs.

## Verifying a download

```bash
shasum -a 256 -c FinXmlProcessor-1.0.0-osx-arm64.dmg.sha256
spctl --assess --type open --context context:primary-signature -v FinXmlProcessor-1.0.0-osx-arm64.dmg
```

## Updates

There is no self-updater in the first release. The Settings page shows the installed version and links to the
Releases page. Add an updater only after its signature-verification and rollback model is designed.
