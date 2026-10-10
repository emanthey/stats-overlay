# Stats Overlay for Linux

A small always-on-top overlay that shows CPU, RAM and GPU usage. It's written in Python 3 with GTK 3 and works the same way as the Windows version.

## Features

- Same layout and colors as the Windows version: always on top, a taskbar entry with the bar-chart icon, drag to move.
- Right-click menu: click-through (lock position), move to a corner, opacity, exit.
- **CPU and RAM** are read from `/proc/stat` and `/proc/meminfo`.
- **GPU and VRAM:** NVIDIA cards are read through `nvidia-smi`, AMD cards from `/sys/class/drm/card*/device/`. Other GPUs, including Intel, show "n/a".
- **Tray icon:** appears when the AppIndicator library (`gir1.2-ayatanaappindicator3-0.1`) is installed. Without a tray, you can turn click-through off by starting the program a second time.
- **Wayland:** the program runs through XWayland, because native Wayland doesn't let a program position itself or stay on top.

## Requirements

Debian / Ubuntu / Mint:

```sh
sudo apt install python3-gi gir1.2-gtk-3.0 gir1.2-ayatanaappindicator3-0.1
```

Fedora:

```sh
sudo dnf install python3-gobject gtk3 libayatana-appindicator-gtk3
```

Arch:

```sh
sudo pacman -S python-gobject gtk3 libayatana-appindicator
```

The AppIndicator package is optional; you only need it for the tray icon.

## Running

```sh
git clone https://github.com/emanthey/stats-overlay.git
cd stats-overlay/linux
python3 statsoverlay.py
```

There is nothing to compile. Optionally, add the program to your application menu so the taskbar shows the right icon:

```sh
sh install.sh
```

This puts `statsoverlay.desktop` and the icon into `~/.local/share`. Then start "Stats Overlay" from your application menu. If you move the cloned folder, run `install.sh` again.

## Files

| File | Purpose |
| --- | --- |
| `statsoverlay.py` | The program |
| `statsoverlay.png` | App and tray icon (the same design as `windows/StatsOverlay.ico`) |
| `install.sh` | Adds the app-menu entry and icon for the current user |
