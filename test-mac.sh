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
elif [[ $# -eq 1 && "$1" == --coverage ]]; then
    codeview_mode=coverage
elif [[ $# -ne 0 ]]; then
    echo 'Usage: ./test-mac.sh [--performance | --coverage]' >&2
    exit 2
fi
command -v dotnet >/dev/null || { echo 'ERROR: Install the .NET SDK specified in global.json.' >&2; exit 1; }
if [[ "$codeview_mode" != performance ]]; then
    command -v python3 >/dev/null || { echo 'ERROR: Python 3 is required for tools tests.' >&2; exit 1; }
    codeview_filter='TestCategory!=Performance'
else
    codeview_filter='TestCategory=Performance'
fi
codeview_results="$codeview_root/artifacts/test-results/macos"
if [[ "$codeview_mode" == coverage ]]; then
    codeview_html="$codeview_results/coverage/html"
    codeview_results="$codeview_results/coverage/current"
    # Replace only generated current-run data, so old runs cannot inflate coverage.
    rm -rf "$codeview_results" "$codeview_html"
fi
codeview_test_args=(--logger "trx;LogFileName=$codeview_mode.trx")
if [[ "$codeview_mode" == coverage ]]; then
    codeview_test_args+=(--collect 'XPlat Code Coverage' --settings "$codeview_root/coverage.runsettings")
fi
mkdir -p "$codeview_results"
dotnet restore codeView.slnx --locked-mode --disable-parallel \
    -p:PublishAot=false -p:CodeViewPerformanceProbe=false -m:1 -nr:false
dotnet test codeView.slnx -c Release --no-restore \
    -p:PublishAot=false -p:PublishTrimmed=false -p:CodeViewPerformanceProbe=false -m:1 -nr:false \
    --filter "$codeview_filter" --results-directory "$codeview_results" \
    "${codeview_test_args[@]}"
if [[ "$codeview_mode" == coverage ]]; then
    dotnet tool restore --tool-manifest "$codeview_root/.config/dotnet-tools.json"
    dotnet tool run reportgenerator -- \
        "-reports:$codeview_results/*/coverage.cobertura.xml" \
        "-targetdir:$codeview_html" "-sourcedirs:$codeview_root/src/codeView" -reporttypes:Html
    test -f "$codeview_html/index.html" || { echo 'ERROR: HTML coverage report was not generated.' >&2; exit 1; }
fi
if [[ "$codeview_mode" != performance ]]; then
    python3 -m unittest discover -s tools -v
fi
echo "Results: $codeview_results/$codeview_mode.trx"
if [[ "$codeview_mode" == coverage ]]; then
    echo "Coverage: $codeview_results/<run-id>/coverage.cobertura.xml, coverage.info"
    echo "HTML: $codeview_html/index.html"
fi
