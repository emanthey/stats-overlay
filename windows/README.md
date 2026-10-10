# Stats Overlay for Windows

A small always-on-top overlay that shows CPU, RAM and GPU usage. It's written in C# with Windows Forms and builds with the C# compiler that ships with Windows, so you don't need Visual Studio or an SDK.

## Building

```bat
git clone https://github.com/emanthey/stats-overlay.git
cd stats-overlay\windows
build.bat
```

This creates `StatsOverlay.exe` in the same folder. Double-click it to run.

## Features

- Always on top, has a taskbar button and a tray icon, and doesn't take focus from other windows.
- Drag to move. The right-click menu (on the overlay or the tray icon) has click-through (lock position), move to a corner, opacity and exit.
- Double-click the tray icon to turn click-through off.
- GPU usage and VRAM come from the same Windows performance counters Task Manager uses.

## Files

| File | Purpose |
| --- | --- |
| `StatsOverlay.cs` | The program |
| `build.bat` | Builds `StatsOverlay.exe` |
| `StatsOverlay.ico` | App icon, embedded into the exe |
| `make_icon.ps1` | Regenerates `StatsOverlay.ico` (`powershell -ExecutionPolicy Bypass -File make_icon.ps1`) |
