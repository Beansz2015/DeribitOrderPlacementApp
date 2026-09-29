#!/usr/bin/env bash
# Builds and runs the order-app-gap proof harness in a scratch directory.
# Needs: .NET 8 SDK (apt-get install -y dotnet-sdk-8.0), nuget.org access, and the app worktree
# at 8232e9e (git worktree add /tmp/app-8232e9e 8232e9e). Arg 1 = worktree path, arg 2 = scenario
# (all | trace | trace-msl-off | trace-emergency-send-fails | emergency-rejected | q1 | restore |
#  close-in-gap | partial | standdown | q5 | mode-flip | atr).
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
WT="${1:-/tmp/app-8232e9e}"
SCEN="${2:-all}"
B="$(mktemp -d)"
cp "$HERE/gen.sh" "$B/gen.sh"
cp "$HERE/OrderAppGap.vbproj.txt" "$B/OrderAppGap.vbproj"
mkdir -p "$B/src"
cp "$HERE/src/Stubs.vb.txt" "$B/src/Stubs.vb"
cp "$HERE/src/Program.vb.txt" "$B/src/Program.vb"
bash "$B/gen.sh" "$WT"
dotnet build "$B/OrderAppGap.vbproj" -nologo -v q 2>&1 | grep -E "error|Build succeeded" || true
dotnet run --project "$B/OrderAppGap.vbproj" --no-build -- "$SCEN"
