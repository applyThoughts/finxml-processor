#!/usr/bin/env bash
# Structural checks for a built bundle. Used by build-app.sh and by CI after signing.
#   scripts/macos/verify-app.sh "<path>/FinXml Processor.app" [--signed]
set -euo pipefail
APP="${1:?app path required}"
MODE="${2:-}"
MACOS="$APP/Contents/MacOS"
fail() { echo "VERIFY FAILED: $*" >&2; exit 1; }

[ -f "$APP/Contents/Info.plist" ] || fail "Info.plist missing"
[ -f "$APP/Contents/Resources/AppIcon.icns" ] || fail "AppIcon.icns missing"
[ -x "$MACOS/FinXmlProcessor.Desktop" ] || fail "desktop executable missing or not executable"
[ -x "$MACOS/finxml" ] || fail "worker executable missing or not executable"
[ -f "$MACOS/samples/profiles/demo-fintech-v1.json" ] || fail "demo profile missing"
[ -f "$MACOS/samples/input/demo-transactions.xml" ] || fail "demo input missing"
plutil -lint "$APP/Contents/Info.plist" >/dev/null || fail "Info.plist is not valid"
/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$APP/Contents/Info.plist" | grep -q '^com\.' || fail "bundle identifier missing"

echo "Running worker smoke test (self-test into a temporary data folder)"
export FINXML_HOME="$(mktemp -d)"
"$MACOS/finxml" self-test --quiet --set Processing:StabilityWindowMilliseconds=0 || fail "worker self-test failed"
"$MACOS/finxml" schedule agent render | grep -q 'run-due' || fail "agent definition rendering failed"
rm -rf "$FINXML_HOME"

if [ "$MODE" = "--signed" ]; then
  echo "Checking code signature"
  codesign --verify --deep --strict --verbose=2 "$APP" || fail "codesign verification failed"
  codesign -dvv "$APP" 2>&1 | grep -q 'flags=.*runtime' || fail "hardened runtime flag missing"
  spctl --assess --type execute --verbose=2 "$APP" || fail "Gatekeeper assessment failed"
fi
echo "Bundle verification passed."
