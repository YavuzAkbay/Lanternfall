using System;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Selene
{
    // Lanternfall is a fork of Selene (github.com/mattxslv/Selene, MIT). The Selene name and its crescent crown logo are not
    // part of that license, so the fork shows its own name and its own mark. Everything that differs from upstream for that
    // lives in this file, and the code keeps Selene's internal names (namespaces, resource keys, file names): that keeps
    // merging upstream fixes easy. Lang.Apply / Lang.T pass every shown text through Text(); Ui.Mark, IntroLogo and
    // PlatformIcons draw DrawMark()/MarkPath instead of the crescent.
    public static class ForkBrand
    {
        public const string Name = "Lanternfall";
        public const string Upper = "LANTERNFALL";

        // where the fork lives: updates come from its releases, problem reports and ideas go to its issues and discussions
        // (never to Selene's: its author doesn't support the fork). No report service of its own: reports open GitHub's form.
        public const string Repo = "YavuzAkbay/Lanternfall";
        public const string RepoUrl = "https://github.com/" + Repo;
        public const string ReportService = "";
        public const string ThemePackage = "Lanternfall.pthm";
        public const string CorePackage = "LanternfallCore.pext";
        public const string BrowserPackage = "LanternfallBrowser.pext";
        public const string Agent = "Lanternfall/1.0 (Playnite theme; github.com/" + Repo + ")";

        // true while the Browser or YouTube is open: the idle screen stays away while you watch or read. The Lanternfall
        // Browser add-on sets an AppDomain slot (same process), but an older add-on doesn't, so the overlays' own
        // visibility (HomeNav shows them through these resources) counts too.
        public static bool MediaActive()
        {
            try { var v = System.AppDomain.CurrentDomain.GetData("Lanternfall.MediaActive"); if (v is bool && (bool)v) return true; }
            catch { }
            try
            {
                var app = System.Windows.Application.Current;
                if (app == null) return false;
                foreach (var key in new[] { "SeleneBrowserOverlayVis", "SeleneYouTubeOverlayVis" })
                {
                    var vis = app.Resources[key];
                    if (vis is System.Windows.Visibility && (System.Windows.Visibility)vis == System.Windows.Visibility.Visible) return true;
                }
            }
            catch { }
            return false;
        }

        // Lanternfall has no background music: UniPlaySong (which plays it, on an audio device it keeps even when Windows
        // switches the default output) is listed as a clash instead of a recommended add-on
        public const string MusicAddon = "UniPlaySong.a1b2c3d4-e5f6-7890-abcd-ef1234567890";

        // "Selene", "Selene's", "Selene Core", "SELENE" in shown text. Credits that must keep the original name write it
        // with a zero-width space ("Sel​ene") so they are left alone.
        private static readonly Regex word = new Regex(@"\bSelene\b", RegexOptions.CultureInvariant);
        private static readonly Regex upper = new Regex(@"\bSELENE\b", RegexOptions.CultureInvariant);

        public static bool Mentions(string s)
        {
            return !string.IsNullOrEmpty(s) && (s.IndexOf("Selene", StringComparison.Ordinal) >= 0 || s.IndexOf("SELENE", StringComparison.Ordinal) >= 0);
        }

        public static string Text(string s)
        {
            if (!Mentions(s)) return s;
            return upper.Replace(word.Replace(s, Name), Upper);
        }

        // ---------------- the lantern mark (72 x 72 units) ----------------
        // A hanging lantern: ring, cap, glass frame, base, and a flame. The ink parts take the text color, the halo around the
        // flame the theme's glow color, like Selene's full moon did. The halo is centred on the flame.
        public const double HaloX = 36, HaloY = 38, HaloSize = 60;
        public const string Ring = "M 36,3.6 A 4.4,4.4 0 1 1 35.99,3.6 Z";
        public const string Cap = "M 28,12 L 44,12 L 51,20.2 L 21,20.2 Z";
        public const string Frame = "M 22.5,19.5 L 49.5,19.5 L 49.5,54 L 22.5,54 Z M 26,23 L 26,50.5 L 46,50.5 L 46,23 Z";
        public const string Base = "M 19,53.4 L 53,53.4 L 49,61 L 23,61 Z";
        public const string Flame = "M 36,27 C 40.5,32 43,35.8 43,40.2 A 7,7 0 0 1 29,40.2 C 29,35.8 31.5,32 36,27 Z";

        // the whole mark as one even-odd shape (the ring drawn as a band), for small icons; its parts only touch (an overlap
        // would be a hole with even-odd), unlike Cap and Base above, which overlap the frame a little so no seam shows
        public const string MarkPath = "F0 M 36,1.8 A 6.2,6.2 0 1 1 35.99,1.8 Z M 36,5.4 A 2.6,2.6 0 1 0 36.01,5.4 Z "
            + "M 28,12 L 44,12 L 51,19.5 L 21,19.5 Z " + Frame + " M 19,54 L 53,54 L 49,61 L 23,61 Z " + Flame;

        // draws the ink parts into a 72 x 72 canvas
        public static void DrawMark(Canvas canvas, Brush ink)
        {
            canvas.Children.Add(new Path { Data = Geometry.Parse(Ring), Stroke = ink, StrokeThickness = 3.6 });
            canvas.Children.Add(new Path { Data = Geometry.Parse(Cap), Fill = ink });
            canvas.Children.Add(new Path { Data = Geometry.Parse("F0 " + Frame), Fill = ink });
            canvas.Children.Add(new Path { Data = Geometry.Parse(Base), Fill = ink });
            canvas.Children.Add(new Path { Data = Geometry.Parse(Flame), Fill = ink });
        }
    }
}
