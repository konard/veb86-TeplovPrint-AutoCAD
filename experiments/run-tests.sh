#!/bin/sh
# Runs the nanoCAD tests on Linux with a newer SDK than global.json pins (temporarily hides global.json).
# Usage: sh experiments/run-tests.sh <log-file>
set -u
cd "$(dirname "$0")/.."
mv global.json /tmp/teplov-global.json.bak
DOTNET_ROLL_FORWARD=Major dotnet test tests/NcadBatchPlot.Tests/NcadBatchPlot.Tests.csproj -c Release -p:EnableWindowsTargeting=true > "$1" 2>&1
code=$?
mv /tmp/teplov-global.json.bak global.json
grep -E "error |Passed!|Failed!|\[FAIL\]" "$1" | head -40
exit $code
