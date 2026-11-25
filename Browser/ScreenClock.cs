// ScreenClock.cs - tells the Browser plugin which monitor it is on and how fast that monitor refreshes.
// Self-contained (no SeleneCore reference): the Browser add-on is a separate plugin and cannot link to Core.
// C# 5, UI-thread friendly; every P/Invoke is guarded. An unknown rate is 0 (never a fabricated 60), so
// callers can fall back to the render loop instead of being stuck on a slow timer.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace SeleneBrowser
{
    /// <summary>
    /// Refresh-rate / monitor-topology probe for the render clock. Mirrors the Core-side Ui.ScreenRate +
    /// Ui.OnFastestScreen logic, but lives in the Browser plugin (which cannot reference SeleneCore types).
    /// CompositionTarget.Rendering ticks at the FASTEST monitor's rate on a multi-monitor PC, so callers
    /// may only ride that loop when the window is on the fastest screen; elsewhere they must pace a timer
    /// to the local display. Results are cached briefly so scrolling does not P/Invoke on every frame.
    /// </summary>
    internal static class ScreenClock
    {
        private const int ENUM_CURRENT_SETTINGS = -1;
        private const uint MONITOR_DEFAULTPRIMARY = 0x00000001;
        private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
        private const double RateCacheSeconds = 3.0;
        private const double UnknownCacheSeconds = 0.25;   // retry quickly after a transient probe failure
        private const double FastestCacheSeconds = 10.0;

        // ---- P/Invoke ----
        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettings(string device, int mode, ref DevMode dm);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettingsEx(string device, int mode, ref DevMode dm, int dwFlags);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X, Y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }

        // Copied from SeleneCore/src/Ui.cs - known-good Unicode DEVMODE layout for EnumDisplaySettings.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DevMode
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra; public int dmFields;
            public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels; public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        // ---- cache ----
        private sealed class RateEntry
        {
            public int Rate;
            public DateTime At;
        }
        private static readonly Dictionary<string, RateEntry> rates = new Dictionary<string, RateEntry>();
        private static int fastestRate;
        private static IntPtr fastestMonitor = IntPtr.Zero;
        private static DateTime fastestAt = DateTime.MinValue;

        /// <summary>
        /// Refresh rate (Hz) of the monitor containing visual. 0 means "unknown" - we never cache a
        /// fabricated 60, so IsFastest can treat a transient probe failure as unknown and choose the
        /// render-loop path instead of locking the scroller onto a slow 60 Hz timer.
        /// </summary>
        public static int Rate(Visual visual)
        {
            IntPtr monitor = MonitorOf(visual);
            string device = DeviceName(monitor);
            string key = monitor.ToString("X") + "|" + (device == null ? "?" : device);

            lock (rates)
            {
                RateEntry hit;
                if (rates.TryGetValue(key, out hit))
                {
                    // Real measurements live for 3 s; a failed probe is retried after 0.25 s so a
                    // hidden/not-yet-loaded window does not poison the cache with a fake 60 for 3 s.
                    double ttl = hit.Rate > 0 ? RateCacheSeconds : UnknownCacheSeconds;
                    if ((DateTime.Now - hit.At).TotalSeconds < ttl) return hit.Rate;
                }
                int r = QueryRate(device);                 // 0 when the device name or mode is unavailable
                if (rates.Count > 16) rates.Clear();       // bounded: one entry per (monitor, device) pair
                rates[key] = new RateEntry { Rate = r, At = DateTime.Now };
                return r;
            }
        }

        /// <summary>
        /// True when the monitor containing visual refreshes at least as fast as every other monitor
        /// (1 Hz tolerance). In that case a render-loop clock runs at the connected display's rate;
        /// on any slower screen in a multi-monitor layout WPF would instead present at the fastest
        /// monitor's rate, so the caller must use its own paced timer.
        /// Cheap and exception-safe: called from SmoothScroller.To() on the UI thread.
        /// </summary>
        public static bool IsFastest(Visual visual)
        {
            IntPtr monitor = MonitorOf(visual);
            int mine = Rate(visual);                       // 0 = unknown, NOT a fabricated 60
            RefreshFastest();

            // We are physically on the monitor that owns the maximum rate. This wins even if a
            // transient probe left 'mine' unknown (0) - the render loop is correct here.
            if (monitor != IntPtr.Zero && fastestMonitor != IntPtr.Zero && monitor == fastestMonitor) return true;
            if (mine <= 0) return true;                    // unknown: prefer the render loop (see note above)
            if (fastestRate <= mine + 1) return true;      // nothing meaningfully faster, or enumeration failed
            return false;
        }

        // ---- helpers ----
        private static void RefreshFastest()
        {
            if ((DateTime.Now - fastestAt).TotalSeconds < FastestCacheSeconds) return;
            int max = 0;
            IntPtr maxMonitor = IntPtr.Zero;
            try
            {
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr hMonitor, IntPtr hdc, ref RECT rc, IntPtr data)
                {
                    int r = QueryRate(DeviceName(hMonitor));
                    if (r > max) { max = r; maxMonitor = hMonitor; }
                    return true;                           // keep enumerating
                }, IntPtr.Zero);
            }
            catch { }
            fastestRate = max;
            fastestMonitor = maxMonitor;
            fastestAt = DateTime.Now;
        }

        private static IntPtr MonitorOf(Visual visual)
        {
            IntPtr hwnd = HandleOf(visual);
            if (hwnd != IntPtr.Zero)
            {
                try
                {
                    IntPtr m = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                    if (m != IntPtr.Zero) return m;
                }
                catch { }
            }
            // No usable HWND: use the visual's screen point (the Win32 equivalent of
            // Screen.FromControl) before giving up on identifying the connected display.
            try
            {
                Point p = visual.PointToScreen(new Point(0, 0));
                IntPtr m = MonitorFromPoint(new POINT { X = (int)p.X, Y = (int)p.Y }, MONITOR_DEFAULTTONEAREST);
                if (m != IntPtr.Zero) return m;
            }
            catch { }
            // Last resort: the primary monitor.
            try { return MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTPRIMARY); }
            catch { return IntPtr.Zero; }
        }

        private static IntPtr HandleOf(Visual visual)
        {
            try
            {
                PresentationSource src = PresentationSource.FromVisual(visual);
                HwndSource hwnd = src as HwndSource;
                if (hwnd != null && hwnd.Handle != IntPtr.Zero) return hwnd.Handle;
            }
            catch { }
            // A hidden / not-yet-loaded ScrollViewer has no PresentationSource, so FromVisual returns
            // null and its handle used to be treated as zero. The overlay is hosted in the app's main
            // window; fall back to that HWND instead of losing the display (which cached a fake 60 Hz
            // for 3 s and permanently picked the wrong clock on a 180 Hz screen).
            try
            {
                Application app = Application.Current;
                Window main = app == null ? null : app.MainWindow;
                if (main != null) return new WindowInteropHelper(main).Handle;
            }
            catch { }
            return IntPtr.Zero;
        }

        private static string DeviceName(IntPtr monitor)
        {
            if (monitor == IntPtr.Zero) return null;
            try
            {
                MONITORINFOEX mi = new MONITORINFOEX();
                mi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
                if (GetMonitorInfo(monitor, ref mi) && !string.IsNullOrEmpty(mi.szDevice)) return mi.szDevice;
            }
            catch { }
            return null;
        }

        // 0 = unknown; callers decide the fallback. EnumDisplaySettingsEx is a fallback for drivers that
        // answer EnumDisplaySettings inconsistently; both use the same DEVMODE layout.
        private static int QueryRate(string device)
        {
            if (string.IsNullOrEmpty(device)) return 0;
            try
            {
                DevMode dm = new DevMode();
                dm.dmSize = (short)Marshal.SizeOf(typeof(DevMode));
                if (EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref dm) && dm.dmDisplayFrequency > 0)
                    return dm.dmDisplayFrequency;
                if (EnumDisplaySettingsEx(device, ENUM_CURRENT_SETTINGS, ref dm, 0) && dm.dmDisplayFrequency > 0)
                    return dm.dmDisplayFrequency;
            }
            catch { }
            return 0;
        }
    }
}
