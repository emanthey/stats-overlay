// StatsOverlay - always-on-top overlay showing RAM, CPU and GPU usage.
// Builds with the C# compiler that ships with Windows (.NET Framework 4.x), see build.bat.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace StatsOverlay
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool createdNew;
            using (var mutex = new Mutex(true, "StatsOverlay_SingleInstance", out createdNew))
            {
                if (!createdNew) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new OverlayForm());
            }
        }
    }

    class Stats
    {
        public float Cpu;
        public float Gpu = -1;          // -1 = unavailable
        public double RamUsedGb, RamTotalGb;
        public double VramUsedGb = -1;  // -1 = unavailable
    }

    class StatsSampler : IDisposable
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        class MEMORYSTATUSEX
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                         ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        PerformanceCounter cpuCounter;
        PerformanceCounterCategory gpuEngineCat, gpuMemCat;
        Dictionary<string, CounterSample> lastGpuSamples;

        public StatsSampler()
        {
            // "% Processor Utility" matches Task Manager; fall back to the classic counter.
            try { cpuCounter = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total"); cpuCounter.NextValue(); }
            catch
            {
                try { cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total"); cpuCounter.NextValue(); }
                catch { cpuCounter = null; }
            }
            try { if (PerformanceCounterCategory.Exists("GPU Engine")) gpuEngineCat = new PerformanceCounterCategory("GPU Engine"); } catch { }
            try { if (PerformanceCounterCategory.Exists("GPU Adapter Memory")) gpuMemCat = new PerformanceCounterCategory("GPU Adapter Memory"); } catch { }
        }

        public Stats Sample()
        {
            var s = new Stats();

            if (cpuCounter != null)
            {
                try { s.Cpu = Math.Min(100f, cpuCounter.NextValue()); } catch { }
            }

            var mem = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(mem))
            {
                const double GB = 1024.0 * 1024 * 1024;
                s.RamTotalGb = mem.ullTotalPhys / GB;
                s.RamUsedGb = (mem.ullTotalPhys - mem.ullAvailPhys) / GB;
            }

            s.Gpu = SampleGpu();
            s.VramUsedGb = SampleVram();
            return s;
        }

        // Same approach as Task Manager: sum utilization per engine type (3D, Copy, VideoDecode, ...)
        // across all processes, then report the busiest engine type.
        float SampleGpu()
        {
            if (gpuEngineCat == null) return -1;
            try
            {
                var data = gpuEngineCat.ReadCategory();
                var util = data["Utilization Percentage"];
                if (util == null) return -1;

                var current = new Dictionary<string, CounterSample>();
                var perEngineType = new Dictionary<string, float>();
                foreach (InstanceData inst in util.Values)
                {
                    current[inst.InstanceName] = inst.Sample;
                    CounterSample prev;
                    if (lastGpuSamples == null || !lastGpuSamples.TryGetValue(inst.InstanceName, out prev)) continue;

                    float v = CounterSample.Calculate(prev, inst.Sample);
                    int idx = inst.InstanceName.IndexOf("engtype_", StringComparison.OrdinalIgnoreCase);
                    string key = idx >= 0 ? inst.InstanceName.Substring(idx) : inst.InstanceName;
                    float sum;
                    perEngineType.TryGetValue(key, out sum);
                    perEngineType[key] = sum + v;
                }
                lastGpuSamples = current;

                float max = 0;
                foreach (var v in perEngineType.Values) if (v > max) max = v;
                return Math.Min(100f, max);
            }
            catch { return -1; }
        }

        double SampleVram()
        {
            if (gpuMemCat == null) return -1;
            try
            {
                var data = gpuMemCat.ReadCategory();
                var used = data["Dedicated Usage"];
                if (used == null) return -1;
                // One instance per adapter; report the adapter using the most memory (the discrete GPU).
                long max = 0;
                foreach (InstanceData inst in used.Values)
                    if (inst.RawValue > max) max = inst.RawValue;
                return max / (1024.0 * 1024 * 1024);
            }
            catch { return -1; }
        }

        public void Dispose()
        {
            if (cpuCounter != null) cpuCounter.Dispose();
        }
    }

    class OverlayForm : Form
    {
        const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_APPWINDOW = 0x40000, WS_EX_NOACTIVATE = 0x8000000;
        const int WS_MINIMIZEBOX = 0x20000;
        const int GWL_EXSTYLE = -20;

        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

        readonly StatsSampler sampler = new StatsSampler();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly ToolStripMenuItem clickThroughItem;
        readonly Font labelFont = new Font("Segoe UI", 9f, FontStyle.Bold);
        readonly Font valueFont = new Font("Consolas", 9.5f, FontStyle.Regular);
        Stats stats = new Stats();
        Thread worker;
        volatile bool running = true;
        bool clickThrough;

        public OverlayForm()
        {
            Text = "Stats Overlay";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = true;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(20, 20, 24);
            Opacity = 0.85;
            DoubleBuffered = true;
            ClientSize = new Size(230, 102);

            var area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(area.Right - Width - 12, area.Top + 12);

            var menu = new ContextMenuStrip();
            clickThroughItem = new ToolStripMenuItem("Click-through (lock position)", null, delegate { SetClickThrough(!clickThrough); });
            menu.Items.Add(clickThroughItem);
            var corners = new ToolStripMenuItem("Move to");
            corners.DropDownItems.Add("Top left", null, delegate { MoveToCorner(false, false); });
            corners.DropDownItems.Add("Top right", null, delegate { MoveToCorner(true, false); });
            corners.DropDownItems.Add("Bottom left", null, delegate { MoveToCorner(false, true); });
            corners.DropDownItems.Add("Bottom right", null, delegate { MoveToCorner(true, true); });
            menu.Items.Add(corners);
            var opacityMenu = new ToolStripMenuItem("Opacity");
            foreach (int pct in new[] { 100, 85, 70, 50 })
            {
                int p = pct;
                opacityMenu.DropDownItems.Add(p + "%", null, delegate { Opacity = p / 100.0; });
            }
            menu.Items.Add(opacityMenu);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { Close(); });

            ContextMenuStrip = menu;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
            tray.Icon = Icon;
            tray.Text = "Stats Overlay";
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
            tray.DoubleClick += delegate { SetClickThrough(false); };

            MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    ReleaseCapture();
                    SendMessage(Handle, 0xA1 /* WM_NCLBUTTONDOWN */, 0x2 /* HTCAPTION */, 0);
                }
            };

            worker = new Thread(SampleLoop) { IsBackground = true, Name = "StatsSampler" };
            worker.Start();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_APPWINDOW | WS_EX_NOACTIVATE; // taskbar button even with NOACTIVATE, don't steal focus
                cp.Style |= WS_MINIMIZEBOX; // lets the taskbar button minimize/restore the borderless window
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        void SampleLoop()
        {
            while (running)
            {
                var s = sampler.Sample();
                try
                {
                    if (IsHandleCreated && !IsDisposed)
                        BeginInvoke((Action)delegate { stats = s; Invalidate(); });
                }
                catch (InvalidOperationException) { }
                Thread.Sleep(1000);
            }
        }

        void SetClickThrough(bool enable)
        {
            clickThrough = enable;
            clickThroughItem.Checked = enable;
            int ex = GetWindowLong(Handle, GWL_EXSTYLE);
            ex = enable ? ex | WS_EX_LAYERED | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
            SetWindowLong(Handle, GWL_EXSTYLE, ex);
            if (enable)
                tray.ShowBalloonTip(2000, "Stats Overlay", "Click-through on. Double-click the tray icon to unlock.", ToolTipIcon.Info);
        }

        void MoveToCorner(bool right, bool bottom)
        {
            var area = Screen.FromControl(this).WorkingArea;
            Location = new Point(right ? area.Right - Width - 12 : area.Left + 12,
                                 bottom ? area.Bottom - Height - 12 : area.Top + 12);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            var s = stats;
            float ramPct = s.RamTotalGb > 0 ? (float)(s.RamUsedGb / s.RamTotalGb * 100) : 0;
            DrawRow(g, 0, "CPU", s.Cpu, string.Format("{0,5:0.0} %", s.Cpu));
            DrawRow(g, 1, "RAM", ramPct, string.Format("{0:0.0}/{1:0.0} GB", s.RamUsedGb, s.RamTotalGb));
            DrawRow(g, 2, "GPU", Math.Max(0, s.Gpu), s.Gpu < 0 ? "n/a" : string.Format("{0,5:0.0} %", s.Gpu));
            if (s.VramUsedGb >= 0)
                TextRenderer.DrawText(g, string.Format("VRAM {0:0.0} GB", s.VramUsedGb), valueFont,
                    new Point(48, 6 + 3 * 24), Color.FromArgb(150, 150, 160));
        }

        void DrawRow(Graphics g, int row, string label, float pct, string value)
        {
            int y = 6 + row * 24;
            Color c = pct >= 85 ? Color.FromArgb(240, 80, 80) : pct >= 60 ? Color.FromArgb(240, 190, 60) : Color.FromArgb(90, 200, 120);

            TextRenderer.DrawText(g, label, labelFont, new Point(8, y + 2), Color.FromArgb(200, 200, 210));
            var bar = new Rectangle(48, y + 15, 172, 4);
            using (var bg = new SolidBrush(Color.FromArgb(55, 55, 62))) g.FillRectangle(bg, bar);
            using (var fg = new SolidBrush(c)) g.FillRectangle(fg, bar.X, bar.Y, bar.Width * Math.Min(100, pct) / 100f, bar.Height);
            TextRenderer.DrawText(g, value, valueFont, new Point(48, y), Color.White);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            running = false;
            tray.Visible = false;
            tray.Dispose();
            sampler.Dispose();
            base.OnFormClosing(e);
        }
    }
}
