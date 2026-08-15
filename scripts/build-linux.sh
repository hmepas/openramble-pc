#!/usr/bin/env bash
# Produce a runnable self-contained linux-x64 build (no installer, no updater).
set -euo pipefail
cd "$(dirname "$0")/.."

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"

OUT="${1:-dist/linux-x64}"

dotnet test --nologo -v q
dotnet publish src/OpenRamble.App.Linux -c Release -r linux-x64 --self-contained -o "$OUT"
# The framework has to be named: asr-bench multi-targets, and the portable target is the one whose
# `mic` records through this platform's capture - the Windows target's records through WASAPI.
dotnet publish src/AsrBench -c Release -f net10.0 -r linux-x64 --self-contained -o "$OUT/tools"

# The recognition engine is native, and a publish that quietly dropped it produces an application
# that starts, downloads 670 MB and then cannot transcribe a word. Cheaper to fail here.
for library in libsherpa-onnx-c-api.so libonnxruntime.so; do
    test -f "$OUT/$library" || { echo "missing native library: $OUT/$library" >&2; exit 1; }
done

echo
echo "Build ready: $OUT"
echo "Run $OUT/OpenRamble.App.Linux (requirements: see the Linux section of README.md)."
