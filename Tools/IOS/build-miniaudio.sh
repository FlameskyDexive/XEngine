#!/bin/bash
# Builds libminiaudioex.dylib for an iOS device or simulator target.
# Mirrors Tools/Android/build-miniaudio.ps1: same upstream pin, same source patch,
# same export-coverage check against MiniAudioExNative's P/Invoke entry points.
#
# Requirements: Xcode (DEVELOPER_DIR or xcode-select), git, network for the first clone.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPOSITORY_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
UPSTREAM="https://github.com/japajoe/miniaudioex.git"
COMMIT="5900f73f647653defb2562ec2efc94fac3897ea3"
SOURCE_PATCH="$SCRIPT_DIR/../Android/miniaudioex-default-playback.patch"
SOURCE_ROOT="${MINIAUDIOEX_SOURCE_ROOT:-$HOME/miniaudioex-source-ios}"
BUILD_ROOT="${MINIAUDIOEX_BUILD_ROOT:-$HOME/miniaudioex-build-ios}"
RID="${1:-${IOS_RUNTIME_IDENTIFIER:-ios-arm64}}"
case "$RID" in
    ios-arm64)
        PLATFORM="iPhoneOS"
        SDK_NAME="iPhoneOS"
        TARGET_TRIPLE="arm64-apple-ios13.0"
        MINIMUM_VERSION_FLAG="-miphoneos-version-min=13.0"
        ;;
    iossimulator-arm64)
        PLATFORM="iPhoneSimulator"
        SDK_NAME="iPhoneSimulator"
        TARGET_TRIPLE="arm64-apple-ios13.0-simulator"
        MINIMUM_VERSION_FLAG="-mios-simulator-version-min=13.0"
        ;;
    *)
        echo "Usage: $0 [ios-arm64|iossimulator-arm64]" >&2
        exit 2
        ;;
esac

DEVELOPER_DIR="${DEVELOPER_DIR:-$(xcode-select -p)}"
if [[ "$DEVELOPER_DIR" == *"CommandLineTools"* || ! -d "$DEVELOPER_DIR/Platforms/iPhoneOS.platform" ]]; then
    for candidate in /Applications/Xcode.app/Contents/Developer /Volumes/*/Applications/Xcode.app/Contents/Developer; do
        if [[ -d "$candidate/Platforms/iPhoneOS.platform" ]]; then
            DEVELOPER_DIR="$candidate"
            break
        fi
    done
fi
SDK="$DEVELOPER_DIR/Platforms/$PLATFORM.platform/Developer/SDKs/$SDK_NAME.sdk"
CLANG="$DEVELOPER_DIR/Toolchains/XcodeDefault.xctoolchain/usr/bin/clang"
NM="$DEVELOPER_DIR/Toolchains/XcodeDefault.xctoolchain/usr/bin/nm"
OTOOL="$DEVELOPER_DIR/Toolchains/XcodeDefault.xctoolchain/usr/bin/otool"
[[ -x "$CLANG" ]] || { echo "clang not found under $DEVELOPER_DIR (set DEVELOPER_DIR to a full Xcode)"; exit 1; }
[[ -x "$NM" ]] || { echo "nm not found under $DEVELOPER_DIR"; exit 1; }
[[ -x "$OTOOL" ]] || { echo "otool not found under $DEVELOPER_DIR"; exit 1; }
[[ -d "$SDK" ]] || { echo "$SDK_NAME.sdk not found under $DEVELOPER_DIR"; exit 1; }

if [[ ! -d "$SOURCE_ROOT/.git" ]]; then
    git clone --filter=blob:none "$UPSTREAM" "$SOURCE_ROOT"
fi
if ! git -C "$SOURCE_ROOT" cat-file -e "$COMMIT^{commit}" 2>/dev/null; then
    git -C "$SOURCE_ROOT" fetch origin "$COMMIT"
fi
git -C "$SOURCE_ROOT" checkout --detach "$COMMIT"
ACTUAL="$(git -C "$SOURCE_ROOT" rev-parse HEAD | tr -d ' ')"
[[ "$ACTUAL" == "$COMMIT" ]] || { echo "miniaudioex source did not resolve to $COMMIT"; exit 1; }
git -C "$SOURCE_ROOT" restore --source="$COMMIT" --staged --worktree -- src/miniaudioex.c
git -C "$SOURCE_ROOT" apply --unidiff-zero --check "$SOURCE_PATCH" || { echo "patch does not apply to $COMMIT"; exit 1; }
git -C "$SOURCE_ROOT" apply --unidiff-zero "$SOURCE_PATCH"

OUTPUT_DIR="$BUILD_ROOT/$RID"
mkdir -p "$OUTPUT_DIR"
OUTPUT="$OUTPUT_DIR/libminiaudioex.dylib"

cd "$SOURCE_ROOT"
"$CLANG" \
    -arch arm64 -target "$TARGET_TRIPLE" "$MINIMUM_VERSION_FLAG" -isysroot "$SDK" \
    -shared -O2 -DNDEBUG -g0 -fPIC \
    -Iinclude -Iinclude/extras \
    -x objective-c src/miniaudio.c src/miniaudioex.c src/extras/miniaudio_libvorbis.c \
    -lobjc \
    -install_name @rpath/libminiaudioex.dylib \
    -framework AudioToolbox -framework CoreAudio -framework CoreFoundation \
    -framework CoreMIDI -framework AVFoundation -framework Foundation \
    -o "$OUTPUT"

# Thin arm64 Mach-O dylib sanity.
file "$OUTPUT"
"$OTOOL" -D "$OUTPUT"
[[ "$(file -b "$OUTPUT")" == *"Mach-O 64-bit dynamically linked shared library arm64"* ]] \
    || { echo "not a thin arm64 dylib"; exit 1; }

# Export coverage: every P/Invoke EntryPoint in MiniAudioExNative must be exported.
# ma_ex_ohos_* entry points are the OpenHarmony hook's surface (Tools/OpenHarmony),
# not part of the iOS library.
IMPORTS=$(grep -o 'EntryPoint = "[^"]*"' "$REPOSITORY_ROOT/XEngine.Runtime/Audio/Native/MiniAudioExNative.cs" \
    | sed 's/EntryPoint = "//;s/"//' | grep -v '^ma_ex_ohos_' | sort -u)
EXPORTS=$("$NM" -gU "$OUTPUT" | awk '{print $3}' | sed 's/^_//' | sort -u)
MISSING=$(comm -23 <(echo "$IMPORTS") <(echo "$EXPORTS"))
if [[ -n "$MISSING" ]]; then
    echo "missing Runtime exports: $MISSING"
    exit 1
fi

DESTINATION="$REPOSITORY_ROOT/Libraries/$RID/native/libminiaudioex.dylib"
mkdir -p "$(dirname "$DESTINATION")"
cp -f "$OUTPUT" "$DESTINATION"
echo "$RID: $(shasum -a 256 "$OUTPUT" | awk '{print $1}')"
echo "installed -> $DESTINATION"
