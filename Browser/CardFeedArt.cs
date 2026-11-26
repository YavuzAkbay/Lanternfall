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

        // Well-known sites keep their own colour; every other site gets a stable hue from its name. The colour is then mixed into the
        // theme's glass colour, so the tile is saturated at the top left and settles into the theme where the title sits.
        private static readonly string[][] Brands =
        {
            new[] { "google", "4285F4" }, new[] { "wikipedia", "6B7F99" }, new[] { "reddit", "FF4500" }, new[] { "github", "8957E5" },
            new[] { "twitch", "9146FF" }, new[] { "youtube", "FF1F3D" }, new[] { "netflix", "E50914" }, new[] { "amazon", "FF9900" },
            new[] { "spotify", "1DB954" }, new[] { "discord", "5865F2" }, new[] { "facebook", "1877F2" }, new[] { "instagram", "E1306C" },
            new[] { "linkedin", "0A66C2" }, new[] { "imdb", "F5C518" }, new[] { "stackoverflow", "F48024" }, new[] { "bing", "0A9E8A" },
            new[] { "duckduckgo", "DE5833" }, new[] { "steampowered", "2A8FCF" }, new[] { "steamcommunity", "2A8FCF" }, new[] { "epicgames", "7D8590" },
            new[] { "gog", "A04BC0" }, new[] { "ign", "D8232A" }, new[] { "gamespot", "E8262B" }, new[] { "pcgamer", "D4001B" },
            new[] { "rockpapershotgun", "C8202F" }, new[] { "eurogamer", "E0392B" }, new[] { "polygon", "5B3CC4" }, new[] { "kotaku", "40B67C" },
            new[] { "bbc", "BB1919" }, new[] { "cnn", "CC0000" }, new[] { "nytimes", "6E6E6E" }, new[] { "theverge", "6C4CF0" },
            new[] { "twitter", "1D9BF0" }, new[] { "x", "5A5F66" }, new[] { "ebay", "E53238" }, new[] { "pinterest", "E60023" },
            new[] { "tiktok", "25D6D0" }, new[] { "wikia", "FA005A" }, new[] { "fandom", "FA005A" }, new[] { "imgur", "1BB76E" },
            new[] { "microsoft", "2F8FE0" }, new[] { "apple", "6E7480" }, new[] { "yahoo", "6001D2" }, new[] { "protonmail", "6D4AFF" },
            new[] { "proton", "6D4AFF" }, new[] { "claude", "D97757" }, new[] { "anthropic", "D97757" }, new[] { "openai", "10A37F" }
        };

        // the name of the site itself: "en.wikipedia.org" -> "wikipedia", "news.bbc.co.uk" -> "bbc"
        internal static string SiteName(string host)
        {
            if (string.IsNullOrEmpty(host)) return "";
            string[] labels = host.ToLowerInvariant().Split('.');
            int i = labels.Length - 2;
            if (labels.Length >= 3 && labels[labels.Length - 1].Length == 2 && labels[labels.Length - 2].Length <= 3) i = labels.Length - 3;   // co.uk, com.au
            return labels[Math.Max(0, i)];
        }

        private static System.Drawing.Color Hsl(double h, double s, double l)
        {
            double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs((h / 60) % 2 - 1)), m = l - c / 2, r, g, b;
            if (h < 60) { r = c; g = x; b = 0; } else if (h < 120) { r = x; g = c; b = 0; } else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; } else if (h < 300) { r = x; g = 0; b = c; } else { r = c; g = 0; b = x; }
            return System.Drawing.Color.FromArgb((int)Math.Round((r + m) * 255), (int)Math.Round((g + m) * 255), (int)Math.Round((b + m) * 255));
        }

        private static System.Drawing.Color BrandColor(string name, int hash)
        {
            foreach (string[] b in Brands)
                if (b[0] == name) return System.Drawing.ColorTranslator.FromHtml("#" + b[1]);
            return Hsl(hash % 360, 0.62, 0.52);
        }

        private static double Luma(System.Drawing.Color c) { return (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0; }

        private static GraphicsPath Rounded(float x, float y, float w, float h, float r)
        {
            var p = new GraphicsPath();
            p.AddArc(x, y, 2 * r, 2 * r, 180, 90); p.AddArc(x + w - 2 * r, y, 2 * r, 2 * r, 270, 90);
            p.AddArc(x + w - 2 * r, y + h - 2 * r, 2 * r, 2 * r, 0, 90); p.AddArc(x, y + h - 2 * r, 2 * r, 2 * r, 90, 90);
            p.CloseFigure();
            return p;
        }

        // One site tile, baked into a 896x504 image (shown at 448x252, so it stays sharp on a 4K screen): a brand-coloured gradient
        // that settles into the theme's glass colour towards the title, a soft glow, a few rings, the site's initial as a big
        // cropped watermark and the same initial on an icon chip.
        private static BitmapImage Site(FeedCard card, Palette pal)
        {
            try
            {
                using (var stream = new MemoryStream())
                {
                    const int W = 896, H = 504;
                    using (var bitmap = new Bitmap(W, H))
                    {
                        using (var g = Graphics.FromImage(bitmap))
                        {
                            int hash = 0;
                            string seed = card.Host ?? card.Key ?? "";
                            string name = SiteName(seed);
                            for (int i = 0; i < name.Length; i++) hash = (hash * 31 + name[i]) & 0x7fffffff;
                            g.SmoothingMode = SmoothingMode.AntiAlias;
                            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                            System.Drawing.Color brand = BrandColor(name, hash);
                            System.Drawing.Color top = Mix(pal.Glass, brand, 0.78), bottom = Mix(pal.Glass, brand, 0.16);
                            using (var field = new LinearGradientBrush(new Rectangle(0, 0, W, H), top, bottom, 62f))
                                g.FillRectangle(field, 0, 0, W, H);

                            // a soft glow from the top left
                            using (var glow = new GraphicsPath())
                            {
                                glow.AddEllipse(-380, -460, 1040, 1040);
                                using (var pg = new PathGradientBrush(glow))
                                {
                                    pg.CenterColor = System.Drawing.Color.FromArgb(70, 255, 255, 255);
                                    pg.SurroundColors = new[] { System.Drawing.Color.FromArgb(0, 255, 255, 255) };
                                    g.FillPath(pg, glow);
                                }
                            }
                            // rings at the top right
                            using (var ring = new Pen(System.Drawing.Color.FromArgb(26, 255, 255, 255), 3f))
                            {
                                g.DrawEllipse(ring, W - 300, -110, 420, 420);
                                g.DrawEllipse(ring, W - 400, -210, 620, 620);
                            }

                            string letter = (name.Length > 0 && char.IsLetterOrDigit(name[0]) ? char.ToUpperInvariant(name[0]) : '•').ToString();
                            // the big watermark, cropped by the right and bottom edges
                            using (var big = new Font("Segoe UI", 560f, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel))
                            using (var wash = new SolidBrush(System.Drawing.Color.FromArgb(34, 255, 255, 255)))
                                g.DrawString(letter, big, wash, new PointF(W - 440, -90));

                            // the icon chip: a rounded square with the initial, light on a saturated tile and dark on a bright one
                            bool bright = Luma(top) > 0.62;
                            System.Drawing.Color chipInk = bright ? System.Drawing.Color.FromArgb(0x16, 0x18, 0x1C) : System.Drawing.Color.White;
                            using (var chip = Rounded(52, 50, 136, 136, 36))
                            {
                                using (var fill = new SolidBrush(System.Drawing.Color.FromArgb(bright ? 60 : 46, chipInk)))
                                    g.FillPath(fill, chip);
                                using (var edge = new Pen(System.Drawing.Color.FromArgb(90, chipInk), 2.5f))
                                    g.DrawPath(edge, chip);
                            }
                            using (var font = new Font("Segoe UI", 84f, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel))
                            using (var text = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                            using (var ink = new SolidBrush(chipInk))
                                g.DrawString(letter, font, ink, new RectangleF(52, 54, 136, 136), text);
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
