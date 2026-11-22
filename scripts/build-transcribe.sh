#!/usr/bin/env bash
# Build the recognition runtime, transcribe.cpp, as shared libraries for linux-x64.
#
# The source is upstream OpenRamble's fork, pinned by commit - the same commit its Mac framework
# and its Rust core build (v0.4.1 + the Parakeet long-form fix + phrase boosting), so a build here
# cannot change what it compiles. The result lands in artifacts/native/linux-x64, where
# src/LocalASR picks it up for every build, test and publish; a second run for the same commit
# does nothing.
#
# CPU only, in the shape of the fork's own portable artifacts: ggml's CPU backend is built once per
# x86 tier (GGML_CPU_ALL_VARIANTS) as a loadable module next to libtranscribe, and the runtime
# picks the best one the processor supports at start (transcribe_init_backends_default). One
# binary that is fast on a new machine and does not die of SIGILL on an old one. No OpenMP and no
# system BLAS, so nothing outside this folder is needed at run time but libc and libstdc++.
#
# win-x64 is not built here. The same CMake invocation works on Windows with MSVC (a Developer
# PowerShell, `cmake -G Ninja` or the Visual Studio generator) and produces transcribe.dll,
# ggml*.dll and the ggml-cpu-*.dll variants; until that runs somewhere and is tested, the Windows
# build keeps sherpa-onnx - see docs/differences-from-macos.md.
set -euo pipefail
cd "$(dirname "$0")/.."

COMMIT=dacdba09be2b8d4dfe97a9d7464c51f462d5f622
REPOSITORY=https://github.com/mikwiseman/transcribe.cpp
OUT=artifacts/native/linux-x64
WORK=artifacts/transcribe.cpp

if [ -f "$OUT/.commit" ] && [ "$(cat "$OUT/.commit")" = "$COMMIT" ] && [ -f "$OUT/libtranscribe.so" ]; then
    echo "transcribe.cpp $COMMIT already built in $OUT"
    exit 0
fi

mkdir -p "$WORK"
SOURCE="$WORK/transcribe.cpp-$COMMIT"
if [ ! -f "$SOURCE/CMakeLists.txt" ]; then
    [ -f "$WORK/$COMMIT.tar.gz" ] \
        || curl -fL --retry 3 -o "$WORK/$COMMIT.tar.gz" "$REPOSITORY/archive/$COMMIT.tar.gz"
    tar -xzf "$WORK/$COMMIT.tar.gz" -C "$WORK"
fi

BUILD="$WORK/build-$COMMIT"
STAGE="$WORK/install-$COMMIT"
rm -rf "$STAGE"
cmake -S "$SOURCE" -B "$BUILD" -G Ninja \
    -DCMAKE_BUILD_TYPE=Release \
    -DTRANSCRIBE_BUILD_SHARED=ON \
    -DTRANSCRIBE_GGML_BACKEND_DL=ON \
    -DGGML_CPU_ALL_VARIANTS=ON \
    -DTRANSCRIBE_X86_CONSERVATIVE=ON \
    -DTRANSCRIBE_USE_OPENMP=OFF \
    -DTRANSCRIBE_USE_SYSTEM_BLAS=OFF \
    -DTRANSCRIBE_BUILD_TESTS=OFF \
    -DTRANSCRIBE_BUILD_EXAMPLES=OFF \
    -DTRANSCRIBE_BUILD_TOOLS=OFF \
    -DCMAKE_INSTALL_PREFIX="$PWD/$STAGE" \
    -DCMAKE_INSTALL_RPATH='$ORIGIN'
cmake --build "$BUILD" --parallel
cmake --install "$BUILD"

# Flat, one real file per library under the name the loader asks for: the SONAME. A publish copies
# symbolic links as whole files, so the versioned chain would ship every library three times.
rm -rf "$OUT"
mkdir -p "$OUT"
find "$STAGE" -name '*.so*' \( -type f -o -type l \) | while read -r library; do
    soname=$(readelf -d "$library" 2>/dev/null | sed -n 's/.*Library soname: \[\(.*\)\]/\1/p')
    name=${soname:-$(basename "$library")}
    [ -f "$OUT/$name" ] || cp -L "$library" "$OUT/$name"
done
# The binding imports "transcribe"; .NET looks for libtranscribe.so.
if [ ! -f "$OUT/libtranscribe.so" ]; then
    mv "$OUT"/libtranscribe.so.* "$OUT/libtranscribe.so"
fi
# MIT asks for the notices to travel with the binaries: the runtime's own and the three libraries
# it vendors into them.
cp "$SOURCE/LICENSE" "$OUT/transcribe-cpp-MIT.txt"
cp "$SOURCE/ggml/LICENSE" "$OUT/ggml-MIT.txt"
cp "$SOURCE/src/third_party/miniz/LICENSE" "$OUT/miniz-MIT.txt"
cp "$SOURCE/src/third_party/silero_vad/LICENSE" "$OUT/silero-vad-MIT.txt"
echo "$COMMIT" > "$OUT/.commit"

echo "transcribe.cpp $COMMIT built into $OUT:"
ls -la "$OUT"
