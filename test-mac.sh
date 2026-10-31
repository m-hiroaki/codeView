#!/bin/bash
# Run from any working directory. Performance workloads require separate preparation.
set -euo pipefail
codeview_root="$(cd "$(dirname "$0")" && pwd)"
cd "$codeview_root"
if [[ "$(uname -s)" != Darwin ]]; then
    echo 'ERROR: Run this script on macOS.' >&2
    exit 1
fi
codeview_mode=unit
if [[ $# -eq 1 && "$1" == --performance ]]; then
    codeview_mode=performance
elif [[ $# -ne 0 ]]; then
    echo 'Usage: ./test-mac.sh [--performance]' >&2
    exit 2
fi
command -v dotnet >/dev/null || { echo 'ERROR: Install the .NET SDK specified in global.json.' >&2; exit 1; }
if [[ "$codeview_mode" == unit ]]; then
    command -v python3 >/dev/null || { echo 'ERROR: Python 3 is required for tools tests.' >&2; exit 1; }
    codeview_filter='TestCategory!=Performance'
else
    codeview_filter='TestCategory=Performance'
fi
codeview_results="$codeview_root/artifacts/test-results/macos"
mkdir -p "$codeview_results"
dotnet restore codeView.slnx --locked-mode --disable-parallel \
    -p:PublishAot=false -p:CodeViewPerformanceProbe=false -m:1 -nr:false
dotnet test codeView.slnx -c Release --no-restore \
    -p:PublishAot=false -p:PublishTrimmed=false -p:CodeViewPerformanceProbe=false -m:1 -nr:false \
    --filter "$codeview_filter" --results-directory "$codeview_results" \
    --logger "trx;LogFileName=$codeview_mode.trx"
if [[ "$codeview_mode" == unit ]]; then
    python3 -m unittest discover -s tools -v
fi
echo "Results: $codeview_results/$codeview_mode.trx"
