#!/usr/bin/env bash
# Produce a runnable self-contained win-x64 build (no installer, no updater).
set -euo pipefail
cd "$(dirname "$0")/.."

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"

OUT="${1:-dist/win-x64}"
# Version = 0.1.<git commit count>, the same number the installer carries - so `app.start
# version=` in a diagnostic log names the build the log came from.
VERSION="${VERSION:-0.1.$(git rev-list --count HEAD)}"

dotnet test --nologo -v q
dotnet publish src/OpenRamble.App -c Release -r win-x64 --self-contained -o "$OUT" -p:Version="$VERSION"
# The framework has to be named: asr-bench multi-targets, and only its Windows target carries
# `mic` - the command that records through the product's own capture.
dotnet publish src/AsrBench -c Release -f net10.0-windows -r win-x64 --self-contained -o "$OUT/tools" -p:Version="$VERSION"

echo
echo "Build ready: $OUT"
echo "Copy the folder into Windows and run OpenRamble.App.exe (see docs/manual-run.md)."
