#!/bin/sh
# Installs SloppyAnimatsu for the current user: the program in ~/.local/share/sloppyanimatsu, a menu entry,
# an icon and a `sloppyanimatsu` launcher in ~/.local/bin.
#
#   ./install.sh              install (set PREFIX to use another prefix than ~/.local)
#   ./install.sh --uninstall  remove what this script installed
#
# The program itself needs no installation: ./SloppyAnimatsu.Viewer runs from the extracted folder.
set -eu

here=$(cd "$(dirname "$0")" && pwd)
prefix=${PREFIX:-"$HOME/.local"}
appdir="$prefix/share/sloppyanimatsu"
launcher="$prefix/bin/sloppyanimatsu"
desktop="$prefix/share/applications/SloppyAnimatsu.desktop"
icon="$prefix/share/icons/hicolor/scalable/apps/sloppyanimatsu.svg"

if [ "${1:-}" = "--uninstall" ]; then
    rm -f "$launcher" "$desktop" "$icon"
    rm -rf "$appdir"
    echo "Removed $appdir, $launcher, the menu entry and the icon."
    exit 0
fi

mkdir -p "$appdir" "$prefix/bin" "$prefix/share/applications" \
         "$prefix/share/icons/hicolor/scalable/apps"

if [ "$here" != "$appdir" ]; then
    cp -R "$here/." "$appdir/"
    rm -f "$appdir/install.sh"
fi
chmod 755 "$appdir/SloppyAnimatsu.Viewer" "$appdir/SloppyAnimatsu.Cli"

cp "$here/sloppyanimatsu.svg" "$icon"

printf '#!/bin/sh\nexec "%s/SloppyAnimatsu.Viewer" "$@"\n' "$appdir" > "$launcher"
chmod 755 "$launcher"

cat > "$desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Sloppy Animatsu
Comment=Rig, skin, animate and export 3D characters
Exec="$appdir/SloppyAnimatsu.Viewer" %f
Icon=sloppyanimatsu
Terminal=false
Categories=Graphics;3DGraphics;
EOF

if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database "$prefix/share/applications" >/dev/null 2>&1 || true
fi

echo "Installed SloppyAnimatsu in $appdir."
echo "Menu entry: $desktop"
echo "Launcher:   $launcher (put $prefix/bin on PATH to run 'sloppyanimatsu')"
