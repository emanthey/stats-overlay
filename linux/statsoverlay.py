#!/usr/bin/env python3
# StatsOverlay - always-on-top overlay showing RAM, CPU and GPU usage (Linux / GTK 3 version).
# Requires PyGObject (python3-gi, gir1.2-gtk-3.0). GPU: NVIDIA via nvidia-smi, AMD via sysfs.
# Tray icon is used when AyatanaAppIndicator3/AppIndicator3 is installed. Without a tray,
# launching the program a second time unlocks click-through mode.
import os
import shutil
import socket
import subprocess
import sys
import threading
import time
import glob

# Positioning and keep-above are not allowed for clients on Wayland; run via XWayland instead.
os.environ.setdefault("GDK_BACKEND", "x11")

import gi
gi.require_version("Gtk", "3.0")
gi.require_version("Gdk", "3.0")
from gi.repository import Gtk, Gdk, GLib
import cairo

AppIndicator = None
for _name in ("AyatanaAppIndicator3", "AppIndicator3"):
    try:
        gi.require_version(_name, "0.1")
        AppIndicator = __import__("gi.repository." + _name, fromlist=[_name])
        break
    except (ValueError, ImportError):
        pass

HERE = os.path.dirname(os.path.abspath(__file__))
ICON_PATH = os.path.join(HERE, "statsoverlay.png")
SOCKET_NAME = "\0StatsOverlay_SingleInstance_%d" % os.getuid()


class Stats:
    def __init__(self):
        self.cpu = 0.0
        self.gpu = -1.0         # -1 = unavailable
        self.ram_used_gb = 0.0
        self.ram_total_gb = 0.0
        self.vram_used_gb = -1.0  # -1 = unavailable


class StatsSampler:
    def __init__(self):
        self.last_cpu = self._read_cpu_times()
        self.nvidia_smi = shutil.which("nvidia-smi")
        self.amd_devices = [os.path.dirname(p) for p in glob.glob("/sys/class/drm/card*/device/gpu_busy_percent")]

    @staticmethod
    def _read_cpu_times():
        try:
            with open("/proc/stat") as f:
                parts = f.readline().split()[1:]
            vals = [int(v) for v in parts]
            idle = vals[3] + (vals[4] if len(vals) > 4 else 0)  # idle + iowait
            return sum(vals[:8]), idle  # exclude guest time, already counted in user
        except (OSError, ValueError, IndexError):
            return None

    def sample(self):
        s = Stats()

        cur = self._read_cpu_times()
        if cur and self.last_cpu:
            total = cur[0] - self.last_cpu[0]
            idle = cur[1] - self.last_cpu[1]
            if total > 0:
                s.cpu = min(100.0, max(0.0, 100.0 * (total - idle) / total))
        self.last_cpu = cur

        try:
            info = {}
            with open("/proc/meminfo") as f:
                for line in f:
                    k, v = line.split(":", 1)
                    info[k] = int(v.split()[0])  # kB
            total = info["MemTotal"]
            avail = info.get("MemAvailable", info.get("MemFree", 0))
            s.ram_total_gb = total / 1024.0 / 1024.0
            s.ram_used_gb = (total - avail) / 1024.0 / 1024.0
        except (OSError, KeyError, ValueError):
            pass

        self._sample_gpu(s)
        return s

    # Report the busiest GPU and the most VRAM used by one adapter (the discrete GPU).
    def _sample_gpu(self, s):
        if self.nvidia_smi:
            try:
                out = subprocess.run(
                    [self.nvidia_smi, "--query-gpu=utilization.gpu,memory.used", "--format=csv,noheader,nounits"],
                    capture_output=True, text=True, timeout=2).stdout
                for line in out.strip().splitlines():
                    util, mem = [x.strip() for x in line.split(",")]
                    s.gpu = max(s.gpu, float(util))
                    s.vram_used_gb = max(s.vram_used_gb, float(mem) / 1024.0)
            except (OSError, ValueError, subprocess.SubprocessError):
                pass
        for dev in self.amd_devices:
            try:
                with open(os.path.join(dev, "gpu_busy_percent")) as f:
                    s.gpu = max(s.gpu, float(f.read()))
                vram = os.path.join(dev, "mem_info_vram_used")
                if os.path.exists(vram):
                    with open(vram) as f:
                        s.vram_used_gb = max(s.vram_used_gb, int(f.read()) / 1024.0 ** 3)
            except (OSError, ValueError):
                pass
        s.gpu = min(100.0, s.gpu)


def bar_color(pct):
    if pct >= 85:
        return (240 / 255, 80 / 255, 80 / 255)
    if pct >= 60:
        return (240 / 255, 190 / 255, 60 / 255)
    return (90 / 255, 200 / 255, 120 / 255)


class OverlayWindow(Gtk.Window):
    WIDTH, HEIGHT = 230, 102

    def __init__(self):
        super().__init__(title="Stats Overlay")
        self.sampler = StatsSampler()
        self.stats = Stats()
        self.click_through = False
        self.indicator = None

        self.set_decorated(False)
        self.set_keep_above(True)
        self.set_skip_taskbar_hint(False)
        self.set_accept_focus(False)
        self.set_resizable(False)
        self.set_default_size(self.WIDTH, self.HEIGHT)
        self.stick()  # show on all workspaces
        if os.path.exists(ICON_PATH):
            self.set_icon_from_file(ICON_PATH)

        area = Gtk.DrawingArea()
        area.connect("draw", self.on_draw)
        self.area = area
        self.add(area)

        self.add_events(Gdk.EventMask.BUTTON_PRESS_MASK)
        self.connect("button-press-event", self.on_button_press)
        self.connect("destroy", Gtk.main_quit)

        self.menu = self.build_menu()
        self.setup_tray()

        self.move_to_corner(True, False)
        self.set_opacity(0.85)

        threading.Thread(target=self.sample_loop, name="StatsSampler", daemon=True).start()

    def build_menu(self):
        menu = Gtk.Menu()
        self.click_through_item = Gtk.CheckMenuItem(label="Click-through (lock position)")
        self.click_through_item.connect("toggled", lambda w: self.set_click_through(w.get_active()))
        menu.append(self.click_through_item)

        move = Gtk.MenuItem(label="Move to")
        sub = Gtk.Menu()
        for label, right, bottom in (("Top left", False, False), ("Top right", True, False),
                                     ("Bottom left", False, True), ("Bottom right", True, True)):
            item = Gtk.MenuItem(label=label)
            item.connect("activate", lambda w, r=right, b=bottom: self.move_to_corner(r, b))
            sub.append(item)
        move.set_submenu(sub)
        menu.append(move)

        opacity = Gtk.MenuItem(label="Opacity")
        sub = Gtk.Menu()
        for pct in (100, 85, 70, 50):
            item = Gtk.MenuItem(label="%d%%" % pct)
            item.connect("activate", lambda w, p=pct: self.set_opacity(p / 100.0))
            sub.append(item)
        opacity.set_submenu(sub)
        menu.append(opacity)

        menu.append(Gtk.SeparatorMenuItem())
        quit_item = Gtk.MenuItem(label="Exit")
        quit_item.connect("activate", lambda w: self.destroy())
        menu.append(quit_item)
        menu.show_all()
        return menu

    def setup_tray(self):
        if AppIndicator is None:
            return
        ind = AppIndicator.Indicator.new("statsoverlay", ICON_PATH if os.path.exists(ICON_PATH) else "utilities-system-monitor",
                                         AppIndicator.IndicatorCategory.APPLICATION_STATUS)
        ind.set_status(AppIndicator.IndicatorStatus.ACTIVE)
        ind.set_title("Stats Overlay")
        ind.set_menu(self.menu)
        self.indicator = ind

    def on_button_press(self, widget, event):
        if event.type != Gdk.EventType.BUTTON_PRESS:
            return False
        if event.button == 1:
            self.begin_move_drag(event.button, int(event.x_root), int(event.y_root), event.time)
        elif event.button == 3:
            self.menu.popup_at_pointer(event)
        return True

    def set_click_through(self, enable):
        self.click_through = enable
        if self.click_through_item.get_active() != enable:
            self.click_through_item.set_active(enable)
        gdk_win = self.get_window()
        if gdk_win is None:
            return
        # An empty input shape lets all mouse events fall through to the windows below.
        gdk_win.input_shape_combine_region(cairo.Region() if enable else None, 0, 0)
        if enable:
            hint = "the tray icon menu" if self.indicator else "launching Stats Overlay again"
            print("Click-through on. Unlock via %s." % hint, file=sys.stderr)

    def move_to_corner(self, right, bottom):
        display = Gdk.Display.get_default()
        gdk_win = self.get_window()
        monitor = (display.get_monitor_at_window(gdk_win) if gdk_win else None) \
            or display.get_primary_monitor() or display.get_monitor(0)
        wa = monitor.get_workarea()
        x = wa.x + wa.width - self.WIDTH - 12 if right else wa.x + 12
        y = wa.y + wa.height - self.HEIGHT - 12 if bottom else wa.y + 12
        self.move(x, y)

    def sample_loop(self):
        while True:
            s = self.sampler.sample()
            GLib.idle_add(self.update_stats, s)
            time.sleep(1)

    def update_stats(self, s):
        self.stats = s
        self.area.queue_draw()
        return False

    def on_draw(self, widget, cr):
        cr.set_source_rgb(20 / 255, 20 / 255, 24 / 255)
        cr.paint()

        s = self.stats
        ram_pct = s.ram_used_gb / s.ram_total_gb * 100 if s.ram_total_gb > 0 else 0
        self.draw_row(cr, 0, "CPU", s.cpu, "%5.1f %%" % s.cpu)
        self.draw_row(cr, 1, "RAM", ram_pct, "%.1f/%.1f GB" % (s.ram_used_gb, s.ram_total_gb))
        self.draw_row(cr, 2, "GPU", max(0, s.gpu), "n/a" if s.gpu < 0 else "%5.1f %%" % s.gpu)
        if s.vram_used_gb >= 0:
            self.draw_text(cr, "VRAM %.1f GB" % s.vram_used_gb, 48, 6 + 3 * 24, (150 / 255, 150 / 255, 160 / 255), False)
        return False

    def draw_text(self, cr, text, x, y, rgb, bold):
        if bold:
            cr.select_font_face("Sans", cairo.FONT_SLANT_NORMAL, cairo.FONT_WEIGHT_BOLD)
            cr.set_font_size(12)
        else:
            cr.select_font_face("Monospace", cairo.FONT_SLANT_NORMAL, cairo.FONT_WEIGHT_NORMAL)
            cr.set_font_size(12.5)
        cr.set_source_rgb(*rgb)
        cr.move_to(x, y + 11)  # y is the top of the text line
        cr.show_text(text)

    def draw_row(self, cr, row, label, pct, value):
        y = 6 + row * 24
        self.draw_text(cr, label, 8, y + 2, (200 / 255, 200 / 255, 210 / 255), True)
        bx, by, bw, bh = 48, y + 15, 172, 4
        cr.set_source_rgb(55 / 255, 55 / 255, 62 / 255)
        cr.rectangle(bx, by, bw, bh)
        cr.fill()
        cr.set_source_rgb(*bar_color(pct))
        cr.rectangle(bx, by, bw * min(100, pct) / 100.0, bh)
        cr.fill()
        self.draw_text(cr, value, 48, y, (1, 1, 1), False)


def main():
    GLib.set_prgname("statsoverlay")  # WM_CLASS, matches StartupWMClass in the .desktop file
    sock = socket.socket(socket.AF_UNIX, socket.SOCK_DGRAM)
    try:
        sock.bind(SOCKET_NAME)
    except OSError:
        # Already running: ask that instance to leave click-through mode, then exit.
        try:
            sock.sendto(b"unlock", SOCKET_NAME)
        except OSError:
            pass
        return

    win = OverlayWindow()

    def on_message(source, condition):
        if sock.recv(64) == b"unlock":
            win.set_click_through(False)
        return True

    GLib.io_add_watch(sock.fileno(), GLib.IO_IN, on_message)
    win.show_all()
    win.move_to_corner(True, False)
    Gtk.main()


if __name__ == "__main__":
    main()
