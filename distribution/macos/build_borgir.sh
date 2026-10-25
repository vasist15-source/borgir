#!/bin/bash
# Builds "Borgir.app": the emulator (dotnet publish) and its native interface (macos/ProjectXUI),
# bundled and signed ad hoc with the entitlements the hypervisor and JIT need.
#
#   distribution/macos/build_borgir.sh [output directory]
#
# Needs the .NET SDK from global.json and the Xcode Command Line Tools. The app goes to
# artifacts/Borgir.app unless another output directory is given. NO_RESTORE=1 skips the NuGet restore, for when the
# package servers can't be reached; every package is in the local cache after the first build.

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
OUTPUT="${1:-$ROOT/artifacts}"
# Absolute, because the library fix-up step runs from another directory.
mkdir -p "$OUTPUT"
OUTPUT="$(cd "$OUTPUT" && pwd)"
VERSION="${VERSION:-1.3.3}"
BUILD="$(git -C "$ROOT" rev-list --count HEAD 2>/dev/null || echo 0)"
COMMIT="$(git -C "$ROOT" rev-parse --short HEAD 2>/dev/null || echo unknown)"

PUBLISH="$OUTPUT/publish"
APP="$OUTPUT/Borgir.app"
CONTENTS="$APP/Contents"
EXECUTABLE="Borgir"
ENTITLEMENTS="$ROOT/distribution/macos/entitlements.xml"
UI_PACKAGE="$ROOT/macos/ProjectXUI"

echo "==> Building the emulator"
rm -rf "$PUBLISH"
RESTORE=""
[ "${NO_RESTORE:-0}" = 1 ] && RESTORE="--no-restore"
# The assembly version must stay 1.3.3.0: the graphics backends load each other by that version.
dotnet publish "$ROOT/src/Ryujinx/Ryujinx.csproj" -c Release -r osx-arm64 --self-contained true \
    -p:Version="$VERSION" \
    -p:AssemblyVersion="$VERSION.0" \
    -p:FileVersion="$VERSION.0" \
    -p:InformationalVersion="$VERSION+borgir.$COMMIT" \
    -p:DebugType=none \
    ${RESTORE:+"$RESTORE"} \
    -o "$PUBLISH"

echo "==> Building the native interface"
swift build -c release --package-path "$UI_PACKAGE"
UI_LIBRARY="$(swift build -c release --package-path "$UI_PACKAGE" --show-bin-path)/libProjectXUI.dylib"

echo "==> Assembling $APP"
rm -rf "$APP"
mkdir -p "$CONTENTS/MacOS" "$CONTENTS/Frameworks" "$CONTENTS/Resources"

cp "$PUBLISH/Ryujinx" "$CONTENTS/MacOS/$EXECUTABLE"
chmod u+x "$CONTENTS/MacOS/$EXECUTABLE"
cp "$PUBLISH"/*.dylib "$CONTENTS/Frameworks/"
cp "$UI_LIBRARY" "$CONTENTS/Frameworks/"
cp "$PUBLISH/THIRDPARTY.md" "$PUBLISH/LICENSE.txt" "$CONTENTS/Resources/"
cp "$ROOT/distribution/macos/Borgir.icns" "$CONTENTS/Resources/Borgir.icns"
sed -e "s/%%VERSION%%/$VERSION/" -e "s/%%BUILD%%/$BUILD/" "$ROOT/distribution/macos/Borgir-Info.plist" > "$CONTENTS/Info.plist"
printf "APPL????" > "$CONTENTS/PkgInfo"

echo "==> Pointing libraries at Contents/Frameworks"
(cd "$ROOT/distribution/macos" && python3 bundle_fix_up.py "$APP" "MacOS/$EXECUTABLE" > /dev/null)

echo "==> Signing (ad hoc)"
find "$CONTENTS/Frameworks" -type f -name "*.dylib" -exec codesign --force --sign - {} \;
codesign --force --sign - --entitlements "$ENTITLEMENTS" "$CONTENTS/MacOS/$EXECUTABLE"
codesign --force --sign - --entitlements "$ENTITLEMENTS" "$APP"
codesign --verify --deep --strict "$APP"

echo "==> Built $APP ($VERSION, build $BUILD, $COMMIT)"
