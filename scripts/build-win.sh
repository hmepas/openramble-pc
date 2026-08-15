#!/usr/bin/env bash
# Produce a runnable self-contained win-x64 build (no installer, no updater), model included.
set -euo pipefail
cd "$(dirname "$0")/.."

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"

OUT="${1:-dist/win-x64}"
# The recognition model ships inside the build (see AppEnvironment.BundledModelDirectory):
# the folder scripts/quantize-parakeet-int4.py writes, holding exactly the files the bundled
# manifest lists. Refusing to build without it beats shipping an installer that asks for a
# download the manifest cannot serve.
MODEL="${OPENRAMBLE_MODEL_BUNDLE:-$HOME/.cache/openramble-win/parakeet-int4}"
for f in encoder.int4.onnx decoder.onnx joiner.onnx tokens.txt; do
    if [ ! -f "$MODEL/$f" ]; then
        echo "error: model bundle incomplete: $MODEL/$f missing." >&2
        echo "       build it with: python3 scripts/quantize-parakeet-int4.py <work-dir> $MODEL" >&2
        exit 1
    fi
done
# Version = 0.1.<git commit count>, the same number the installer carries - so `app.start
# version=` in a diagnostic log names the build the log came from.
VERSION="${VERSION:-0.1.$(git rev-list --count HEAD)}"

dotnet test --nologo -v q
dotnet publish src/OpenRamble.App -c Release -r win-x64 --self-contained -o "$OUT" -p:Version="$VERSION"
# The framework has to be named: asr-bench multi-targets, and only its Windows target carries
# `mic` - the command that records through the product's own capture.
dotnet publish src/AsrBench -c Release -f net10.0-windows -r win-x64 --self-contained -o "$OUT/tools" -p:Version="$VERSION"

mkdir -p "$OUT/model"
for f in encoder.int4.onnx decoder.onnx joiner.onnx tokens.txt; do
    cp -f "$MODEL/$f" "$OUT/model/$f"
done

echo
echo "Build ready: $OUT"
echo "Copy the folder into Windows and run OpenRamble.App.exe (see docs/manual-run.md). The model is in $OUT/model."
