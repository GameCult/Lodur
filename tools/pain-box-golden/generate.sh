#!/usr/bin/env bash
# Regenerates tests/Lodur.Core.Tests/pain-box-golden.tsv from Bad Day Simulator's pain box at 9f59764.
# Usage: tools/pain-box-golden/generate.sh <path to a BadDaySimulator checkout at 9f59764>
set -euo pipefail
bds="${1:?path to a BadDaySimulator checkout at 9f59764}"
want=9f597647a608a6c6e44473cc9f1399983de14616
have="$(git -C "$bds" rev-parse HEAD)"
[ "$have" = "$want" ] || { echo "BadDaySimulator is at $have, need $want" >&2; exit 1; }
[ -z "$(git -C "$bds" status --porcelain -- src)" ] || { echo "BadDaySimulator has local changes under src/; the table must come from $want as committed" >&2; exit 1; }
here="$(cd "$(dirname "$0")" && pwd)"
dotnet run --project "$here/PainBoxGolden.csproj" -p:BdsRoot="$(cd "$bds" && pwd -W 2>/dev/null || pwd)" -- "$here/../../tests/Lodur.Core.Tests/pain-box-golden.tsv"
