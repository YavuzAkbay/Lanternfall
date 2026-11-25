// StartPage.cs - the browser's start page: a themed launchpad drawn like Lanternfall's home screen. The page is plain HTML/CSS, so it is
// rendered from the live palette each time it is built (BrowserControl re-runs it when the theme changes): the theme's wallpaper
// (a downscaled JPEG, cached) or its backdrop gradient with the accent beam, the same three scrims as Atmosphere, a big search pill and
// a grid of speed-dial tiles (bookmarks, most visited, recent). Sizes are in design units (1920 wide) via --u, so it looks the same on any
// screen. The spatial-navigation shim draws the focus ring; the page only scales the focused tile (window.__nomadFocus).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SeleneBrowser
{
    internal static class StartPage
    {
        /// <summary>id of the search input: the shim reports it, and the browser opens its address keyboard instead of a plain text field.</summary>
        public const string SearchId = "lf-search";

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ----- wallpaper (downscaled once per wallpaper) -----

        private static string wallKey, wallUri;

        private static string WallpaperUri()
        {
            ImageSource src = Theme.Wallpaper;
            if (src == null) { wallKey = null; wallUri = null; return null; }
            string key = Theme.ThemeId + "|" + RuntimeHelpers.GetHashCode(src);
            if (key == wallKey) return wallUri;
            wallKey = key;
            wallUri = null;
            try
            {
                BitmapSource bmp = src as BitmapSource;
                if (bmp == null)
                {
                    // a drawing: paint it once at 1280 wide
                    double w = src.Width > 0 ? src.Width : 1920, h = src.Height > 0 ? src.Height : 1080;
                    int pw = 1280, ph = Math.Max(1, (int)Math.Round(1280 * h / w));
                    var dv = new DrawingVisual();
                    using (DrawingContext dc = dv.RenderOpen()) dc.DrawImage(src, new Rect(0, 0, pw, ph));
                    var rtb = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(dv);
                    bmp = rtb;
                }
                BitmapSource scaled = bmp;
                if (bmp.PixelWidth > 1280)
                {
                    double k = 1280.0 / bmp.PixelWidth;
                    scaled = new TransformedBitmap(bmp, new ScaleTransform(k, k));
                }
                var rgb = new FormatConvertedBitmap(scaled, PixelFormats.Bgr24, null, 0);
                // the page is itself a data: URL (WebView2 refuses ~2 MB), so keep the picture small: lower the quality until it fits
                foreach (int quality in new[] { 72, 58, 44, 32 })
                {
                    var enc = new JpegBitmapEncoder { QualityLevel = quality };
                    enc.Frames.Add(BitmapFrame.Create(rgb));
                    using (var ms = new MemoryStream())
                    {
                        enc.Save(ms);
                        wallUri = "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
                    }
                    if (wallUri.Length <= 600000) break;
                    wallUri = null;
                }
            }
            catch (Exception) { wallUri = null; }
            return wallUri;
        }

        // ----- page -----

        // a length in design units: 1 unit = 1/1920 of the page's width, whatever the screen and the zoom
        private static string P(double n) { return "calc(var(--u)*" + n.ToString("0.##", Inv) + ")"; }

        private static string Rgba(Color c, double a) { return Theme.Css(c, a); }

        public static string Build(List<BrowserEntry> bookmarks, List<BrowserEntry> history, string searchAction)
        {
            Color ink = Theme.InkColor, glass = Theme.GlassColor, scrim = Theme.ScrimColor, focus = Theme.FocusColor;
            Color glyph = Theme.ColorOf("GlyphColor", "#FF9CCBFF");
            bool light = Theme.IsLight;
            string wall = WallpaperUri();
            var sb = new StringBuilder(4096);
            sb.Append("<!doctype html><html><head><meta charset='utf-8'><title>Home</title><style>");
            sb.Append(":root{color-scheme:").Append(light ? "light" : "dark").Append(";--u:calc(100vw/1920);--ink:").Append(Rgba(ink, 1))
              .Append(";--ink60:").Append(Rgba(ink, 0.6)).Append(";--line:").Append(Rgba(ink, 0.16)).Append(";--glass:").Append(Rgba(glass, 0.72))
              .Append(";--hot:").Append(Rgba(focus, 0.16)).Append(";--solid:").Append(Rgba(glass, 0.94)).Append(";--accent:").Append(Theme.AccentHex).Append("}");
            sb.Append("*{box-sizing:border-box}html{scrollbar-width:none;background:").Append(Rgba(scrim, 1)).Append("}::-webkit-scrollbar{display:none}");
            sb.Append("body{margin:0;min-height:100vh;color:var(--ink);font:400 ").Append(P(22)).Append(" 'Segoe UI',sans-serif;overflow-x:hidden}");
            // backdrop: the wallpaper, or the theme's gradient with the accent beam / wash / haze (as on the home screen)
            sb.Append(".bg,.sh{position:fixed;left:0;top:0;right:0;bottom:0;pointer-events:none}.bg{z-index:0;background:");
            if (wall != null) sb.Append("url(").Append(wall).Append(") center/cover no-repeat");
            else
            {
                double k = light ? 0.55 : 1;
                sb.Append("radial-gradient(ellipse 20% 85% at 14% -8%,").Append(Rgba(glyph, 0.42 * k)).Append(" 0%,").Append(Rgba(glyph, 0.14 * k)).Append(" 50%,").Append(Rgba(glyph, 0)).Append(" 100%),")
                  .Append("radial-gradient(ellipse 60% 110% at 16% -12%,").Append(Rgba(glyph, 0.16 * k)).Append(" 0%,").Append(Rgba(glyph, 0)).Append(" 100%),")
                  .Append("radial-gradient(ellipse 95% 34% at 50% 102%,").Append(Rgba(glyph, 0.22 * k)).Append(" 0%,").Append(Rgba(glyph, 0)).Append(" 100%),")
                  .Append(Theme.BackdropCss());
            }
            sb.Append("}");
            // the three scrims of Atmosphere (shade 0.6): left, bottom, top
            sb.Append(".sh{z-index:1;background:")
              .Append("linear-gradient(90deg,").Append(Rgba(scrim, 0.528)).Append(" 0%,").Append(Rgba(scrim, 0.30)).Append(" 50%,").Append(Rgba(scrim, 0.09)).Append(" 100%),")
              .Append("linear-gradient(180deg,").Append(Rgba(scrim, 0)).Append(" 40%,").Append(Rgba(scrim, 0.54)).Append(" 100%),")
              .Append("linear-gradient(180deg,").Append(Rgba(scrim, 0.54)).Append(" 0%,").Append(Rgba(scrim, 0.264)).Append(" 10%,").Append(Rgba(scrim, 0)).Append(" 26%)}");
            sb.Append("main{position:relative;z-index:2;padding:").Append(P(36)).Append(" ").Append(P(64)).Append(" ").Append(P(90))
              .Append(";animation:rise .24s cubic-bezier(.215,.61,.355,1) both}@keyframes rise{from{opacity:0;transform:translateY(").Append(P(10)).Append(")}to{opacity:1;transform:none}}");
            sb.Append("header{display:flex;align-items:flex-end;justify-content:space-between;margin-bottom:").Append(P(28)).Append("}");
            sb.Append("h1{margin:0;font:600 ").Append(P(56)).Append(" 'Segoe UI Variable Display','Segoe UI',sans-serif}");
            sb.Append(".clk{text-align:right;line-height:1.15}.clk b{display:none;font:400 ").Append(P(30)).Append(" 'Segoe UI Variable Display','Segoe UI',sans-serif}")
              .Append(".clk span{font-size:").Append(P(20)).Append(";color:var(--ink60)}");
            // search pill: the whole input is the pill, so the focus ring sits around all of it
            sb.Append("form{margin:0;position:relative}form svg{position:absolute;left:").Append(P(34)).Append(";top:50%;width:").Append(P(34)).Append(";height:").Append(P(34))
              .Append(";margin-top:").Append(P(-17)).Append(";fill:var(--ink60);pointer-events:none}");
            sb.Append("input{display:block;width:100%;height:").Append(P(80)).Append(";border-radius:").Append(P(40)).Append(";border:1px solid var(--line);background:var(--glass);color:var(--ink);")
              .Append("font:400 ").Append(P(26)).Append(" 'Segoe UI Variable Display','Segoe UI',sans-serif;padding:0 ").Append(P(40)).Append(" 0 ").Append(P(92)).Append(";outline:none}")
              .Append("input::placeholder{color:var(--ink60);opacity:1}");
            sb.Append("h2{margin:").Append(P(46)).Append(" 0 ").Append(P(18)).Append(";font:600 ").Append(P(20)).Append(" 'Segoe UI',sans-serif;letter-spacing:.14em;text-transform:uppercase;color:var(--ink60)}");
            sb.Append(".g{display:grid;grid-template-columns:repeat(5,minmax(0,1fr));gap:").Append(P(22)).Append("}");
            sb.Append(".g a{display:flex;flex-direction:column;justify-content:space-between;gap:").Append(P(14)).Append(";min-height:").Append(P(170)).Append(";padding:").Append(P(22)).Append(" ").Append(P(24))
              .Append(";border-radius:").Append(P(18)).Append(";background:var(--glass);border:1px solid var(--line);color:var(--ink);text-decoration:none;overflow:hidden;")
              .Append("transition:transform .18s cubic-bezier(.215,.61,.355,1),background .18s}");
            sb.Append(".g a.on{transform:scale(1.04);background:linear-gradient(var(--hot),var(--hot)),var(--solid)}");
            sb.Append(".g i{flex:none;width:").Append(P(56)).Append(";height:").Append(P(56)).Append(";border-radius:50%;background:var(--line);display:flex;align-items:center;justify-content:center;")
              .Append("font:600 ").Append(P(26)).Append(" 'Segoe UI Variable Display','Segoe UI',sans-serif;font-style:normal;color:var(--ink)}");
            sb.Append(".g b{display:-webkit-box;-webkit-box-orient:vertical;-webkit-line-clamp:2;overflow:hidden;font:600 ").Append(P(22)).Append("/1.25 'Segoe UI Variable Display','Segoe UI',sans-serif}");
            sb.Append(".g small{display:block;margin-top:").Append(P(4)).Append(";font:400 ").Append(P(19)).Append(" 'Segoe UI',sans-serif;color:var(--ink60);white-space:nowrap;overflow:hidden;text-overflow:ellipsis}");
            sb.Append("</style></head><body><div class='bg'></div><div class='sh'></div><main>");
            sb.Append("<header><h1>Browser</h1><div class='clk'><b id='t'></b><span id='d'></span></div></header>");
            sb.Append("<form action='").Append(WebUtility.HtmlEncode(searchAction)).Append("' method='get'>")
              .Append("<svg viewBox='0 0 24 24'><path d='M15.5 14h-.79l-.28-.27A6.47 6.47 0 0 0 16 9.5 6.5 6.5 0 1 0 9.5 16c1.61 0 3.09-.59 4.23-1.57l.27.28v.79l5 4.99L20.49 19l-4.99-5zm-6 0C7.01 14 5 11.99 5 9.5S7.01 5 9.5 5 14 7.01 14 9.5 11.99 14 9.5 14z'/></svg>")
              .Append("<input id='").Append(SearchId).Append("' name='q' placeholder='Search or type a URL' autocomplete='off'></form>");

            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<BrowserEntry> marks = bookmarks.Take(15).ToList();
            foreach (BrowserEntry e in marks) used.Add(HostOf(e.Url));
            List<BrowserEntry> often = MostVisited(history, used);
            foreach (BrowserEntry e in often) used.Add(HostOf(e.Url));
            Tiles(sb, "Bookmarks", marks);
            Tiles(sb, "Most visited", often);
            Tiles(sb, "Recent", history.Take(10).ToList());
            sb.Append("</main><script>(function(){var t=document.getElementById('t'),d=document.getElementById('d');")
              .Append("function tick(){var n=new Date();t.textContent=n.toLocaleTimeString([],{hour:'numeric',minute:'2-digit'});")
              .Append("d.textContent=n.toLocaleDateString([],{weekday:'long',month:'long',day:'numeric'});}tick();setInterval(tick,20000);")
              // the navigation shim calls this whenever its highlight moves: only the tile under it grows
              .Append("window.__nomadFocus=function(el){var o=document.querySelector('.g a.on');if(o&&o!==el)o.classList.remove('on');")
              .Append("if(el&&el.matches&&el.matches('.g a'))el.classList.add('on');};})();</script></body></html>");
            return sb.ToString();
        }

        private static string HostOf(string url)
        {
            Uri u;
            if (!string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out u) && !string.IsNullOrEmpty(u.Host))
                return u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? u.Host.Substring(4) : u.Host;
            return url ?? "";
        }

        // sites visited on more than one page (history keeps one row per page), skipping what is already shown as a bookmark
        private static List<BrowserEntry> MostVisited(List<BrowserEntry> history, HashSet<string> skip)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var first = new Dictionary<string, BrowserEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (BrowserEntry e in history)
            {
                if (e == null || string.IsNullOrEmpty(e.Url) || !e.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                string h = HostOf(e.Url);
                if (h.Length == 0 || skip.Contains(h)) continue;
                int n;
                counts.TryGetValue(h, out n);
                counts[h] = n + 1;
                if (!first.ContainsKey(h)) first[h] = e;
            }
            var result = new List<BrowserEntry>();
            foreach (KeyValuePair<string, int> kv in counts.Where(c => c.Value >= 2).OrderByDescending(c => c.Value).Take(5))
            {
                Uri u;
                string root = Uri.TryCreate(first[kv.Key].Url, UriKind.Absolute, out u) ? u.Scheme + "://" + u.Host + "/" : first[kv.Key].Url;
                result.Add(new BrowserEntry { Title = kv.Key, Url = root });
            }
            return result;
        }

        private static void Tiles(StringBuilder sb, string heading, List<BrowserEntry> items)
        {
            if (items.Count == 0) return;
            sb.Append("<h2>").Append(WebUtility.HtmlEncode(heading)).Append("</h2><div class='g'>");
            foreach (BrowserEntry e in items)
            {
                string host = HostOf(e.Url);
                string name = string.IsNullOrWhiteSpace(e.Title) || e.Title == e.Url ? host : e.Title.Trim();
                string letter = "?";
                foreach (char ch in name) if (char.IsLetterOrDigit(ch)) { letter = ch.ToString().ToUpperInvariant(); break; }
                sb.Append("<a href='").Append(WebUtility.HtmlEncode(e.Url)).Append("'><i>").Append(WebUtility.HtmlEncode(letter)).Append("</i><span><b>")
                  .Append(WebUtility.HtmlEncode(name)).Append("</b><small>").Append(string.Equals(name, host, StringComparison.OrdinalIgnoreCase) ? "&nbsp;" : WebUtility.HtmlEncode(host)).Append("</small></span></a>");
            }
            sb.Append("</div>");
        }
    }
}
