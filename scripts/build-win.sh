#!/usr/bin/env bash
# Produce a runnable self-contained win-x64 build (no installer, no updater).
set -euo pipefail
cd "$(dirname "$0")/.."

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"

OUT="${1:-dist/win-x64}"

dotnet test --nologo -v q
dotnet publish src/OpenRamble.App -c Release -r win-x64 --self-contained -o "$OUT"
dotnet publish src/AsrBench -c Release -r win-x64 --self-contained -o "$OUT/tools"

echo
echo "Build ready: $OUT"
echo "Copy the folder into Windows and run OpenRamble.App.exe (see docs/manual-run.md)."
