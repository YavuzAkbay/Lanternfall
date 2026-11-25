// CardFeedArt.cs - turns the shaped feed cards into the plain resources the theme reads (SeleneYouTubeFeed* / SeleneBrowserFeed*).
// YouTube thumbnails go through the same ThumbCache the YouTube screen uses; site tiles are drawn locally, so no network favicons
// (and no UI-thread I/O). Written from CardInfo's background refresh, with image callbacks marshalled back to the dispatcher.
// The site tiles are drawn in the picked Lanternfall theme's colours (glass, focus, ink), read once on the UI thread per refresh.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SeleneBrowser
{
    internal static class CardFeedArt
    {
        /// <summary>The theme colours a tile is drawn with (opaque, System.Drawing).</summary>
        private sealed class Palette
        {
            public System.Drawing.Color Glass = System.Drawing.Color.FromArgb(0x14, 0x18, 0x1F);
            public System.Drawing.Color Focus = System.Drawing.Color.FromArgb(0xFF, 0xFF, 0xFF);
            public System.Drawing.Color OnFocus = System.Drawing.Color.FromArgb(0x11, 0x11, 0x11);
            public System.Drawing.Color Ink = System.Drawing.Color.FromArgb(0xFF, 0xFF, 0xFF);
        }

        private static Palette lastGood = new Palette();

        private static System.Drawing.Color Opaque(System.Windows.Media.Color c)
        {
            return System.Drawing.Color.FromArgb(c.R, c.G, c.B);
        }

        // Application.Resources belong to the UI thread: read them there (never throws; falls back to the last good palette)
        private static Palette ReadPalette()
        {
            Palette p = new Palette();
            p.Glass = Opaque(Theme.ColorOf("SeleneGlassColor", "#FF14181F"));
            p.Focus = Opaque(Theme.ColorOf("SeleneFocusColor", "#FFFFFFFF"));
            p.OnFocus = Opaque(Theme.ColorOf("SeleneOnFocusColor", "#FF111111"));
            p.Ink = Opaque(Theme.ColorOf("SeleneInkColor", "#FFFFFFFF"));
            return p;
        }

        private static Palette Capture(Dispatcher ui)
        {
            try
            {
                if (ui == null) return lastGood;
                Palette got = null;
                if (ui.CheckAccess()) got = ReadPalette();
                else ui.Invoke(DispatcherPriority.Normal, TimeSpan.FromSeconds(2), new Action(delegate { got = ReadPalette(); }));
                if (got != null) lastGood = got;
            }
            catch { }
            return lastGood;
        }

        private static System.Drawing.Color Mix(System.Drawing.Color a, System.Drawing.Color b, double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return System.Drawing.Color.FromArgb(
                (int)Math.Round(a.R + (b.R - a.R) * t), (int)Math.Round(a.G + (b.G - a.G) * t), (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        // prefix + "" / "Title" / "Sub" / "Key" (0..2) and prefix + "EmptyVis".
        // set() writes one resource (marshalled by CardInfo); a thumbnail that arrives later uses the same set().
        public static void Write(Action<string, object> set, List<FeedCard> cards, string prefix, Dispatcher ui)
        {
            Palette pal = Capture(ui);
            set(prefix + "EmptyVis", cards == null || cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed);
            for (int i = 0; i < CardFeed.Tiles; i++)
            {
                FeedCard card = cards != null && i < cards.Count ? cards[i] : null;
                set(prefix + i, null);
                set(prefix + "Title" + i, card == null ? "" : card.Title);
                set(prefix + "Sub" + i, card == null ? "" : card.Sub);
                set(prefix + "Key" + i, card == null ? "" : card.Key);
                set(prefix + "TileVis" + i, card == null ? Visibility.Collapsed : Visibility.Visible);   // no card, no tile
                if (card == null) continue;
                if (card.ImageUrl != null && ui != null)
                {
                    // ThumbCache's dictionaries are only safe on the UI thread, so call Load there (not on CardInfo's worker).
                    string url = card.ImageUrl, p = prefix, key = card.Key;
                    int index = i;
                    ui.BeginInvoke(new Action(delegate
                    {
                        ThumbCache.Load(url, 896, ui, delegate(BitmapImage image)
                        {
                            // a slower download from an older refresh must not overwrite this video's tile
                            var app = Application.Current;
                            if (app == null) return;
                            object live = app.Resources[p + "Key" + index];
                            if (live != null && live.ToString() == key) set(p + index, image);
                        });
                    }));
                }
                else set(prefix + i, Site(card, pal));
            }
        }

        // The start page uses a first-letter circle for every site; here it is baked into one 448x252 image per tile.
        // A glass-coloured field with a hint of the focus colour, a focus-coloured disc and the letter in the on-focus colour.
        private static BitmapImage Site(FeedCard card, Palette pal)
        {
            try
            {
                using (var stream = new MemoryStream())
                {
                    using (var bitmap = new Bitmap(448, 252))
                    {
                        using (var g = Graphics.FromImage(bitmap))
                        {
                            int hash = 0;
                            string seed = card.Host ?? card.Key ?? "";
                            for (int i = 0; i < seed.Length; i++) hash = (hash * 31 + seed[i]) & 0x7fffffff;
                            g.SmoothingMode = SmoothingMode.AntiAlias;
                            // each site keeps its own variation (the hash), inside the palette: a different amount of focus colour / ink
                            System.Drawing.Color disc = Mix(pal.Focus, pal.Ink, ((hash >> 5) % 100) / 100.0 * 0.3);
                            System.Drawing.Color field = Mix(pal.Glass, pal.Focus, 0.10 + (hash % 24) / 100.0);
                            g.Clear(field);
                            using (var brush = new SolidBrush(System.Drawing.Color.FromArgb(215 + (hash >> 9) % 40, disc)))
                                g.FillEllipse(brush, 160, 34, 128, 128);
                            string letter = (seed.Length > 0 ? char.ToUpperInvariant(seed[0]) : '?').ToString();
                            using (var font = new Font("Segoe UI", 62, System.Drawing.FontStyle.Bold))
                            using (var text = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                            using (var ink = new SolidBrush(pal.OnFocus))
                                g.DrawString(letter, font, ink, new RectangleF(160, 34, 128, 128), text);
                        }
                        bitmap.Save(stream, ImageFormat.Png);
                    }
                    stream.Position = 0;
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.StreamSource = stream;
                    image.EndInit();
                    image.Freeze();
                    return image;
                }
            }
            catch { return null; }
        }
    }
}
