#!/bin/sh
# Installs a menu entry and icon for the current user so Stats Overlay shows up in the
# application launcher and gets its own icon in the taskbar/dock.
set -e
DIR="$(cd "$(dirname "$0")" && pwd)"
chmod +x "$DIR/statsoverlay.py"
mkdir -p "$HOME/.local/share/applications" "$HOME/.local/share/icons/hicolor/256x256/apps"
cp "$DIR/statsoverlay.png" "$HOME/.local/share/icons/hicolor/256x256/apps/statsoverlay.png"
cat > "$HOME/.local/share/applications/statsoverlay.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Stats Overlay
Comment=Always-on-top CPU, RAM and GPU usage overlay
Exec=python3 "$DIR/statsoverlay.py"
Icon=statsoverlay
Terminal=false
Categories=System;Monitor;
StartupWMClass=statsoverlay
EOF
echo "Installed. Start 'Stats Overlay' from your application menu, or run: python3 $DIR/statsoverlay.py"
echo "Needs: python3-gi gir1.2-gtk-3.0 (optional tray: gir1.2-ayatanaappindicator3-0.1)"
