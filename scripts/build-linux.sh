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

# The transcribe.cpp runtime is native code built from a pinned commit; a second run for the same
# commit is a no-op. src/LocalASR carries the result into every build below.
./scripts/build-transcribe.sh

dotnet test --nologo -v q
dotnet publish src/OpenRamble.App.Linux -c Release -r linux-x64 --self-contained -o "$OUT" -p:Version="$VERSION"
# The framework has to be named: asr-bench multi-targets, and the portable target is the one whose
# `mic` records through this platform's capture - the Windows target's records through WASAPI.
dotnet publish src/AsrBench -c Release -f net10.0 -r linux-x64 --self-contained -o "$OUT/tools" -p:Version="$VERSION"

# The recognition engines are native, and a publish that quietly dropped one produces an
# application that starts, downloads 740 MB and then cannot transcribe a word. Cheaper to fail
# here. transcribe.cpp runs Parakeet Ultra - libtranscribe needs its two ggml libraries and at
# least the baseline CPU backend module; sherpa-onnx runs Parakeet v3 for a machine that still
# holds it.
for library in libtranscribe.so libggml.so.0 libggml-base.so.0 libggml-cpu-x64.so \
    libsherpa-onnx-c-api.so libonnxruntime.so; do
    test -f "$OUT/$library" || { echo "missing native library: $OUT/$library" >&2; exit 1; }
done

echo
echo "Build ready: $OUT"
echo "Run $OUT/OpenRamble.App.Linux (requirements: see the Linux section of README.md)."
