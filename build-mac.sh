#!/bin/bash
# Build a self-contained macOS .app. Run on macOS with the SDK in global.json.
set -euo pipefail

codeview_root="$(cd "$(dirname "$0")" && pwd)"
cd "$codeview_root"
if [[ "$(uname -s)" != Darwin ]]; then
    echo 'ERROR: Run this script on macOS.' >&2
    exit 1
fi
if [[ $# -gt 1 ]]; then
    echo 'Usage: ./build-mac.sh [osx-arm64|osx-x64]' >&2
    exit 2
fi
codeview_rid="${1:-}"
if [[ -z "$codeview_rid" ]]; then
    case "$(uname -m)" in
        arm64) codeview_rid=osx-arm64 ;;
        x86_64) codeview_rid=osx-x64 ;;
        *) echo 'ERROR: Unsupported host architecture.' >&2; exit 2 ;;
    esac
fi
case "$codeview_rid" in
    osx-arm64|osx-x64) ;;
    *) echo 'ERROR: Specify osx-arm64 or osx-x64.' >&2; exit 2 ;;
esac
command -v dotnet >/dev/null || { echo 'ERROR: Install the .NET SDK specified in global.json.' >&2; exit 1; }

codeview_output="$codeview_root/artifacts/publish/$codeview_rid"
codeview_build="$codeview_root/artifacts/build/$codeview_rid"
mkdir -p "$codeview_build" "$codeview_output"
codeview_stage="$(mktemp -d "$codeview_build/stage.XXXXXX")"
trap 'rm -rf "$codeview_stage"' EXIT

# Validate the checked-in managed graph, then derive the RID-specific graph in staging.
dotnet restore src/codeView/codeView.csproj --locked-mode --disable-parallel -m:1 -nr:false
cp src/codeView/packages.lock.json "$codeview_stage/publish.packages.lock.json"
dotnet publish src/codeView/codeView.csproj -c Release -r "$codeview_rid" \
    --self-contained true --artifacts-path "$codeview_build" -o "$codeview_stage/payload" \
    -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false \
    -p:CodeViewPerformanceProbe=false -p:RestoreLockedMode=false \
    "-p:NuGetLockFilePath=$codeview_stage/publish.packages.lock.json" -m:1 -nr:false

if [[ ! -x "$codeview_stage/payload/codeView" ]]; then
    echo 'ERROR: Publish did not produce the codeView executable.' >&2
    exit 1
fi
mkdir -p "$codeview_stage/codeView.app/Contents/MacOS"
cp -R "$codeview_stage/payload/." "$codeview_stage/codeView.app/Contents/MacOS/"
cp build/Info.plist "$codeview_stage/codeView.app/Contents/Info.plist"
# Replace only the generated app, after publish and packaging have succeeded.
rm -rf "$codeview_output/codeView.app"
mv "$codeview_stage/codeView.app" "$codeview_output/codeView.app"
cp "$codeview_stage/publish.packages.lock.json" "$codeview_output/publish.packages.lock.json"
echo "Built: $codeview_output/codeView.app"
echo 'This build is not distribution-signed or notarized.'
