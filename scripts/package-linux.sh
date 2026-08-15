#!/usr/bin/env bash
# Package the current build for manual distribution:
#   releases/openramble-linux-x64-<version>.tar.zst - unpack and run the binary inside
# The same archive is what the AUR package downloads from a GitHub release, so the desktop entry
# and the icon travel in it: see packaging/linux/PKGBUILD.
# Version = 0.1.<git commit count>, the same number the Windows packaging stamps.
set -euo pipefail
cd "$(dirname "$0")/.."

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"

VERSION="0.1.$(git rev-list --count HEAD)"
NAME="openramble-linux-x64-$VERSION"
OUT=releases
STAGE="dist/$NAME"
mkdir -p "$OUT"

./scripts/build-linux.sh

rm -rf "$STAGE"
cp -a dist/linux-x64 "$STAGE"
cp LICENSE "$STAGE/"
# Everything readable, nothing group-writable: these files are about to become root's on somebody
# else's machine, and a build directory's umask is no business of theirs.
chmod -R a+rX,u+w,go-w "$STAGE"
install -Dm644 packaging/linux/openramble.desktop "$STAGE/share/applications/openramble.desktop"
# The compositor-keybind sample travels with the build: it is the alternative to the input group,
# and a person who cannot join that group is exactly the person who has only the unpacked archive
# in front of them. See the "Hotkey without the input group" section of the README.
install -Dm644 packaging/linux/hyprland-example.conf \
    "$STAGE/share/openramble/hyprland-example.conf"

# The icon is extracted rather than kept as a second file in the tree: the .ico is the one copy of
# the art (the Windows executable's icon, the tray picture, the Avalonia resource), and its
# 256-pixel plane is already a PNG - so this is a copy out, not a conversion.
python3 - "$STAGE/share/icons/hicolor/256x256/apps/openramble.png" <<'EOF'
import pathlib
import struct
import sys

icon = pathlib.Path("src/OpenRamble.App/Assets/OpenRamble.ico").read_bytes()
planes = []
for index in range(struct.unpack_from("<H", icon, 4)[0]):
    size, offset = struct.unpack_from("<II", icon, 6 + 16 * index + 8)
    plane = icon[offset:offset + size]
    if plane.startswith(b"\x89PNG"):
        planes.append(plane)

if not planes:
    sys.exit("the icon carries no PNG plane to install")

target = pathlib.Path(sys.argv[1])
target.parent.mkdir(parents=True, exist_ok=True)
target.write_bytes(max(planes, key=len))
EOF

# Reproducible field order: same tree in, same archive out, whoever runs it.
tar --sort=name --numeric-owner --owner=0 --group=0 \
    -I 'zstd -19 -T0' -cf "$OUT/$NAME.tar.zst" -C dist "$NAME"
rm -rf "$STAGE"

echo
echo "Version $VERSION:"
ls -la "$OUT"
echo
echo "For the AUR package: bump pkgver in packaging/linux/PKGBUILD to $VERSION and run updpkgsums"
echo "once the archive is attached to the GitHub release."
