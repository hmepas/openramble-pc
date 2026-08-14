#!/usr/bin/env bash
# Package the current build for friends-and-family distribution:
#   releases/OpenRamble-win-x64-<version>.zip   - unzip and run
#   releases/OpenRamble-win-Setup.exe           - Velopack installer; running a
#                                                 newer Setup.exe upgrades the
#                                                 install in place
# Version = 0.1.<git commit count>. Unsigned: Windows SmartScreen will warn on
# first run ("More info" -> "Run anyway") - tell the recipients.
set -euo pipefail
cd "$(dirname "$0")/.."

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT/tools:$DOTNET_ROOT:$PATH"

VERSION="0.1.$(git rev-list --count HEAD)"
OUT=releases
mkdir -p "$OUT"

./scripts/build-win.sh

( cd dist && python3 -m zipfile -c "../$OUT/OpenRamble-win-x64-$VERSION.zip" win-x64 )

vpk '[win]' pack --yes \
    --packId OpenRamble \
    --packVersion "$VERSION" \
    --packTitle OpenRamble \
    --packAuthors "OpenRamble port" \
    --packDir dist/win-x64 \
    --mainExe OpenRamble.App.exe \
    --outputDir "$OUT" \
    --runtime win-x64

echo
echo "Version $VERSION:"
ls -la "$OUT"
