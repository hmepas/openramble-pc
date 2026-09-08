#!/usr/bin/env bash
# Produce a runnable self-contained linux-x64 build (no installer, no updater).
set -euo pipefail
cd "$(dirname "$0")/.."

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"

OUT="${1:-dist/linux-x64}"

# Version = 0.1.<git commit count>, the same number scripts/package-linux.sh puts in the archive
# name - so `app.start version=` in a diagnostic log names the build the log came from.
VERSION="${VERSION:-0.1.$(git rev-list --count HEAD)}"

dotnet test --nologo -v q
dotnet publish src/OpenRamble.App.Linux -c Release -r linux-x64 --self-contained -o "$OUT" -p:Version="$VERSION"
# The framework has to be named: asr-bench multi-targets, and the portable target is the one whose
# `mic` records through this platform's capture - the Windows target's records through WASAPI.
dotnet publish src/AsrBench -c Release -f net10.0 -r linux-x64 --self-contained -o "$OUT/tools" -p:Version="$VERSION"

# The recognition engine is native, and a publish that quietly dropped it produces an application
# that starts, downloads 720 MB and then cannot transcribe a word. Cheaper to fail here.
for library in libsherpa-onnx-c-api.so libonnxruntime.so; do
    test -f "$OUT/$library" || { echo "missing native library: $OUT/$library" >&2; exit 1; }
done

echo
echo "Build ready: $OUT"
echo "Run $OUT/OpenRamble.App.Linux (requirements: see the Linux section of README.md)."
