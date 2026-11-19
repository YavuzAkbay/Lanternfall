using System;
using System.Windows;

namespace Selene
{
    // Picture sizes in real screen pixels. Selene's screens are laid out at 1920 x 1080 and Playnite stretches them to the
    // screen (a Viewbox), so a picture asked for "1920 wide" fills 3840 real pixels on a 4K TV: decoded at 1920 it came out
    // blown up and soft. Widths passed around the code stay in layout units; the decoders run them through Width() here.
    public static class PixelSize
    {
        private static double scale = 1;
        private static bool measured, hooked;

        // screen pixels per layout unit (1 on a 1080p screen at 100%, 1.33 at 1440p, 2 on 4K); readable from any thread
        public static double Scale
        {
            get
            {
                if (!measured) TryMeasure();
                return scale;
            }
        }

        // set by whoever has measured a real element (TileCovers does for every cover)
        public static void Measured(double value)
        {
            if (value <= 0) return;
            if (!measured || Math.Abs(value - scale) > 0.01) HomeNav.Log("pictures: screen scale " + Math.Round(value, 3));
            scale = value; measured = true;
        }

        // a decode width in layout units -> in screen pixels (0 = full size stays 0)
        public static int Width(int width)
        {
            if (width <= 0) return width;
            double s = Scale;
            return s <= 1.01 ? width : (int)Math.Ceiling(width * s);
        }

        private static void TryMeasure()
        {
            try
            {
                var app = Application.Current;
                if (app == null || !app.Dispatcher.CheckAccess()) return;   // (off the UI thread: the last known scale)
                var win = app.MainWindow;
                if (win == null || !win.IsLoaded) return;
                if (!hooked) { hooked = true; win.SizeChanged += (s, e) => Measured(TileCovers.PixelScale(win)); win.DpiChanged += (s, e) => Measured(TileCovers.PixelScale(win)); }
                Measured(TileCovers.PixelScale(win));
            }
            catch { }
        }
    }
}
