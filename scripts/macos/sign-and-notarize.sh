#!/usr/bin/env bash
# Signs a built bundle inside-out with the Developer ID Application certificate, packages it as a DMG,
# submits it to Apple notarization, staples the ticket and verifies the result.
#
#   scripts/macos/sign-and-notarize.sh "<path>/FinXml Processor.app" <output-dir> <version> <rid>
#
# Required environment (set by the release workflow from repository secrets; never committed):
#   APPLE_SIGNING_IDENTITY   e.g. "Developer ID Application: Example Org (TEAMID)"
#   APPLE_TEAM_ID
#   APPLE_API_KEY_ID, APPLE_API_ISSUER_ID, APPLE_API_KEY_PATH  (App Store Connect API key .p8 file)
# The certificate must already be imported into the active keychain (see release.yml).
set -euo pipefail

APP="${1:?app path required}"
OUT_DIR="${2:?output dir required}"
VERSION="${3:?version required}"
RID="${4:?rid required}"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
ENTITLEMENTS="$ROOT/scripts/macos/entitlements.plist"
: "${APPLE_SIGNING_IDENTITY:?}" "${APPLE_TEAM_ID:?}" "${APPLE_API_KEY_ID:?}" "${APPLE_API_ISSUER_ID:?}" "${APPLE_API_KEY_PATH:?}"

sign() {
  codesign --force --options runtime --timestamp --entitlements "$ENTITLEMENTS" --sign "$APPLE_SIGNING_IDENTITY" "$@"
}

echo "==> Signing nested native libraries and executables (inside-out)"
# Native dylibs first, then the two Mach-O executables, then the bundle itself. --deep is deliberately not used.
find "$APP/Contents/MacOS" -type f \( -name '*.dylib' -o -name '*.so' \) -print0 | while IFS= read -r -d '' f; do sign "$f"; done
find "$APP/Contents/MacOS" -type f -perm -u+x ! -name '*.dylib' ! -name '*.so' -print0 | while IFS= read -r -d '' f; do
  if file "$f" | grep -q 'Mach-O'; then sign "$f"; fi
done
echo "==> Signing bundle"
sign "$APP"

echo "==> Verifying signature"
"$ROOT/scripts/macos/verify-app.sh" "$APP" --signed

DMG="$OUT_DIR/FinXmlProcessor-$VERSION-$RID.dmg"
echo "==> Creating DMG $DMG"
STAGING="$(mktemp -d)"
cp -R "$APP" "$STAGING/"
ln -s /Applications "$STAGING/Applications"
hdiutil create -volname "FinXml Processor" -srcfolder "$STAGING" -ov -format UDZO "$DMG"
rm -rf "$STAGING"
sign "$DMG"

echo "==> Submitting for notarization"
xcrun notarytool submit "$DMG" --key "$APPLE_API_KEY_PATH" --key-id "$APPLE_API_KEY_ID" --issuer "$APPLE_API_ISSUER_ID" --wait --output-format json > "$OUT_DIR/notarization-$RID.json" || {
  echo "Notarization failed; fetching log" >&2
  SUBMISSION_ID="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1])).get("id",""))' "$OUT_DIR/notarization-$RID.json" 2>/dev/null || true)"
  if [ -n "$SUBMISSION_ID" ]; then
    xcrun notarytool log "$SUBMISSION_ID" --key "$APPLE_API_KEY_PATH" --key-id "$APPLE_API_KEY_ID" --issuer "$APPLE_API_ISSUER_ID" || true
  fi
  exit 1
}
grep -q '"status": *"Accepted"' "$OUT_DIR/notarization-$RID.json" || { echo "Notarization was not accepted" >&2; cat "$OUT_DIR/notarization-$RID.json"; exit 1; }

echo "==> Stapling and final verification"
xcrun stapler staple "$DMG"
xcrun stapler validate "$DMG"
spctl --assess --type open --context context:primary-signature --verbose=2 "$DMG"
shasum -a 256 "$DMG" > "$DMG.sha256"
echo "Signed and notarized: $DMG"
