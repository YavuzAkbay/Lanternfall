// PlayerOverlayWindow.cs - keeps the YouTube player's WPF controls above a windowed (HWND) WebView2.
// The player page runs in the windowed WebView2 because the composition control copies every frame into WPF through
// screen capture: video came out softer and stuck near 30 fps. A child HWND always draws over WPF content in the same
// window ("airspace"), so the controls live in this transparent, click-through, never-activated window owned by
// Playnite's window and kept exactly over the player area.
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace SeleneBrowser
{
    internal sealed class PlayerOverlayWindow
    {
        private readonly FrameworkElement anchor;   // the player area the overlay covers
        private readonly UIElement content;
        private Window win, owner;
        private Rect last = Rect.Empty;

        public PlayerOverlayWindow(FrameworkElement anchor, UIElement content)
        {
            this.anchor = anchor;
            this.content = content;
            anchor.IsVisibleChanged += delegate { Sync(); };
            anchor.LayoutUpdated += delegate { if (win != null) PlaceAndShow(); };
            anchor.Unloaded += delegate { Hide(); };
        }

        private void Sync()
        {
            if (anchor.IsVisible) Show(); else Hide();
        }

        private void Show()
        {
            Window o = Window.GetWindow(anchor);
            if (o == null) return;
            if (win == null || owner != o)
            {
                if (win != null) { win.Content = null; win.Close(); }
                bool wire = owner != o;
                owner = o;
                win = new Window
                {
                    WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true,
                    Background = Brushes.Transparent, ShowInTaskbar = false, ShowActivated = false, Focusable = false,
                    Topmost = false, IsHitTestVisible = false, Owner = o, Content = content,
                    UseLayoutRounding = true, SnapsToDevicePixels = true
                };
                win.SourceInitialized += delegate
                {
                    IntPtr h = new WindowInteropHelper(win).Handle;
                    int ex = GetWindowLong(h, GWL_EXSTYLE);
                    SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED);
                };
                if (wire)
                {
                    o.LocationChanged += delegate { if (win != null) PlaceAndShow(); };
                    o.SizeChanged += delegate { if (win != null) PlaceAndShow(); };
                    o.Closed += delegate { if (win != null) { win.Content = null; win.Close(); win = null; } };
                }
            }
            last = Rect.Empty;
            PlaceAndShow();
        }

        // Never show the overlay before it has been placed: an unpositioned window renders the chrome
        // at its default size for a frame (the focused tab becomes a gigantic button when opening).
        private void PlaceAndShow()
        {
            if (win == null) return;
            if (Place() && !win.IsVisible && anchor.IsVisible) win.Show();
        }

        /// <summary>Closes the window (YouTube left); the next Show() builds a new one.</summary>
        public void Close()
        {
            if (win == null) return;
            try { win.Content = null; win.Close(); } catch { }
            win = null;
        }

        private void Hide()
        {
            if (win != null && win.IsVisible) win.Hide();
        }

        // the anchor's screen rectangle, in the overlay's own DIPs. True when geometry is valid.
        private bool Place()
        {
            if (win == null || owner == null || !anchor.IsVisible || anchor.ActualWidth <= 0) return false;
            PresentationSource src = PresentationSource.FromVisual(anchor);
            if (src == null || src.CompositionTarget == null) return false;
            Point a, b;
            try
            {
                a = anchor.PointToScreen(new Point(0, 0));
                b = anchor.PointToScreen(new Point(anchor.ActualWidth, anchor.ActualHeight));
            }
            catch (InvalidOperationException) { return false; }
            Matrix fromDevice = src.CompositionTarget.TransformFromDevice;
            Point da = fromDevice.Transform(a), db = fromDevice.Transform(b);
            Rect r = new Rect(da, db);
            if (r == last) return true;
            last = r;
            win.Left = r.Left; win.Top = r.Top; win.Width = r.Width; win.Height = r.Height;
            return true;
        }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);
    }
}
