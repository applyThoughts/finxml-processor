#!/usr/bin/env bash
# Builds a self-contained FinXml Processor.app for one architecture.
#
#   scripts/macos/build-app.sh <osx-arm64|osx-x64> [output-dir] [version]
#
# Produces: <output-dir>/<rid>/FinXml Processor.app (unsigned) and a ZIP next to it.
# Signing and notarization are separate steps (sign-and-notarize.sh) so unsigned test builds
# can be produced without any Apple credentials.
set -euo pipefail

RID="${1:?runtime identifier required (osx-arm64 or osx-x64)}"
OUT_DIR="${2:-artifacts}"
VERSION="${3:-$(grep -o '<VersionPrefix>[^<]*' Directory.Build.props | sed 's/<VersionPrefix>//')}"
BUILD_NUMBER="${GITHUB_RUN_NUMBER:-0}"
APP_NAME="FinXml Processor"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
STAGE="$OUT_DIR/$RID"
APP="$STAGE/$APP_NAME.app"
CONTENTS="$APP/Contents"
MACOS="$CONTENTS/MacOS"
RESOURCES="$CONTENTS/Resources"

echo "==> Publishing desktop and worker for $RID (version $VERSION, build $BUILD_NUMBER)"
rm -rf "$STAGE"
mkdir -p "$MACOS" "$RESOURCES"

# Restore once for all configured runtime identifiers (matches the committed lock files exactly), then publish
# per RID with --no-restore: a RID-specific restore would narrow the RID set and fail locked-mode restore.
dotnet restore "$ROOT/FinXmlProcessor.sln" --locked-mode
PUBLISH_ARGS=(-c Release -r "$RID" --self-contained true --no-restore -p:PublishSingleFile=false -p:PublishTrimmed=false -p:IncludeNativeLibrariesForSelfExtract=false -p:Version="$VERSION" -p:InformationalVersion="$VERSION+${GITHUB_SHA:-local}")

dotnet publish "$ROOT/src/FinXmlProcessor.Desktop/FinXmlProcessor.Desktop.csproj" "${PUBLISH_ARGS[@]}" -o "$STAGE/publish-desktop"
dotnet publish "$ROOT/src/FinXmlProcessor.Worker/FinXmlProcessor.Worker.csproj" "${PUBLISH_ARGS[@]}" -o "$STAGE/publish-worker"

echo "==> Assembling bundle"
# Both executables share one runtime folder: copy the desktop publish output, then add the worker's
# managed assemblies and its apphost. Shared native/runtime files are identical for the same RID.
cp -R "$STAGE/publish-desktop/." "$MACOS/"
cp -R "$STAGE/publish-worker/." "$MACOS/"
chmod +x "$MACOS/FinXmlProcessor.Desktop" "$MACOS/finxml"

# Samples travel with the app so self-test and the demo profile work out of the box.
mkdir -p "$MACOS/samples/profiles" "$MACOS/samples/input"
cp "$ROOT"/samples/profiles/*.json "$MACOS/samples/profiles/"
cp "$ROOT"/samples/input/demo-transactions.xml "$MACOS/samples/input/"

sed -e "s/__VERSION__/$VERSION/" -e "s/__BUILD_NUMBER__/$BUILD_NUMBER/" "$ROOT/scripts/macos/Info.plist.template" > "$CONTENTS/Info.plist"
printf 'APPL????' > "$CONTENTS/PkgInfo"

echo "==> Building icon"
ICONSET="$STAGE/AppIcon.iconset"
mkdir -p "$ICONSET"
SRC_ICON="$ROOT/src/FinXmlProcessor.Desktop/Assets/icon-1024.png"
for size in 16 32 64 128 256 512; do
  sips -z $size $size "$SRC_ICON" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  sips -z $double $double "$SRC_ICON" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$RESOURCES/AppIcon.icns"

rm -rf "$STAGE/publish-desktop" "$STAGE/publish-worker" "$ICONSET"

echo "==> Verifying layout"
"$ROOT/scripts/macos/verify-app.sh" "$APP"

ZIP="$STAGE/FinXmlProcessor-$VERSION-$RID-unsigned.zip"
echo "==> Zipping to $ZIP"
ditto -c -k --sequesterRsrc --keepParent "$APP" "$ZIP"
shasum -a 256 "$ZIP" > "$ZIP.sha256"
echo "Done: $APP"
