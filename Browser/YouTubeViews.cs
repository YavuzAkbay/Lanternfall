// YouTubeViews.cs - building blocks of the YouTube screen: thumbnail cache, view state, cards, picker list,
// spinner and smooth scrolling. UI-thread only unless stated. C# 5.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace SeleneBrowser
{
    internal enum CardKind { Video, Channel, Playlist }

    /// <summary>One screen worth of content (a tab, a channel page, a playlist) with its paging state.</summary>
    internal sealed class ViewState
    {
        public int Tab = -1;                 // rail tab this state belongs to (-1 = pushed page)
        public CardKind Kind = CardKind.Video;
        public string Title = "";
        public string Status = "";           // "Updating..." text, shown top right
        public string Empty = "Nothing here yet.";
        public string Error = "";
        public bool Loading, Loaded;
        public int Token;                    // bumped on every (re)load so stale results are ignored
        public int Focus;
        public long LastApply;
        public string Continuation = "";
        public ChannelItem Header;
        public List<VideoItem> Videos = new List<VideoItem>();
        public List<VideoItem> AllVideos = new List<VideoItem>();   // Subscriptions: unfiltered feed (Videos = what the grid shows)
        public List<VideoItem> Continue = new List<VideoItem>();    // Subscriptions: videos with a saved resume position
        public string Filter = "All";                              // Subscriptions chip: All / Today / Unwatched / Continue
        public string Format = "Videos";                           // Channel page chip: Videos / Shorts / Live / Playlists
        public List<ChannelItem> Channels = new List<ChannelItem>();
        public List<PlaylistItem> Playlists = new List<PlaylistItem>();
        public Func<string, System.Threading.Tasks.Task<PageResult<VideoItem>>> Loader;

        public int Count
        {
            get { return Kind == CardKind.Video ? Videos.Count : (Kind == CardKind.Channel ? Channels.Count : Playlists.Count); }
        }
        public bool HasMore { get { return !string.IsNullOrEmpty(Continuation); } }
    }

    /// <summary>Downloads and decodes thumbnails on 4 background threads; callers get the frozen image on the UI thread.</summary>
    /// <summary>Lets the grid let go of a far-away card's picture (a decoded 640 px thumbnail is ~0.9 MB in a 32-bit process) and fetch it back.</summary>
    internal sealed class ThumbHandle
    {
        public Image Img;
        public string Url;
        public int Width;
        public Dispatcher Ui;
        public CardArt Art;   // told when the picture is back (the ambient backdrop may be waiting for it)
        public void Release() { if (Img.Source != null) Img.Source = null; }
        public void Restore()
        {
            if (Img.Source != null) return;
            Image target = Img;
            CardArt art = Art;
            ThumbCache.Load(Url, Width, Ui, delegate(BitmapImage b) { target.Source = b; if (art != null) art.Notify(); });
        }
    }

    internal static class ThumbCache
    {
        private static readonly Dictionary<string, BitmapImage> cache = new Dictionary<string, BitmapImage>();
        private static readonly Dictionary<string, List<Action<BitmapImage>>> waiting = new Dictionary<string, List<Action<BitmapImage>>>();
        private static readonly BlockingCollection<Action> queue = new BlockingCollection<Action>();
        private static bool started;

        public static void Load(string url, int width, Dispatcher ui, Action<BitmapImage> done)
        {
            if (string.IsNullOrEmpty(url)) return;
            if (url.StartsWith("//", StringComparison.Ordinal)) url = "https:" + url;
            string key = width + "|" + url;
            BitmapImage hit;
            if (cache.TryGetValue(key, out hit)) { done(hit); return; }
            List<Action<BitmapImage>> list;
            if (waiting.TryGetValue(key, out list)) { list.Add(done); return; }
            list = new List<Action<BitmapImage>>();
            list.Add(done);
            waiting[key] = list;
            StartWorkers();
            string u = url;
            queue.Add(delegate
            {
                BitmapImage bmp = null;
                try { bmp = Fetch(u, width); } catch { }
                ui.BeginInvoke(new Action(delegate
                {
                    List<Action<BitmapImage>> cbs;
                    waiting.TryGetValue(key, out cbs);
                    waiting.Remove(key);
                    if (bmp == null || cbs == null) return;
                    if (cache.Count > 100) cache.Clear();   // 640 px thumbs are ~0.9 MB decoded
                    cache[key] = bmp;
                    foreach (Action<BitmapImage> cb in cbs) { try { cb(bmp); } catch { } }
                }));
            });
        }

        /// <summary>Forgets the decoded images (YouTube closed); cards still showing one keep their own reference.</summary>
        public static void Trim() { cache.Clear(); }

        private static void StartWorkers()
        {
            if (started) return;
            started = true;
            for (int i = 0; i < 4; i++)
            {
                Thread t = new Thread(delegate()
                {
                    foreach (Action a in queue.GetConsumingEnumerable()) { try { a(); } catch { } }
                });
                t.IsBackground = true;
                t.Start();
            }
        }

        private static byte[] Download(string url)
        {
            HttpWebRequest r = Http.Create("GET", url, 10000, null);
            using (WebResponse resp = r.GetResponse())
            using (Stream s = resp.GetResponseStream())
            using (MemoryStream ms = new MemoryStream())
            {
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        private static BitmapImage Fetch(string url, int width)
        {
            byte[] data = null;
            // video thumbnails default to mqdefault.jpg (320x180, soft on a big screen): try the 1280x720 one first,
            // it is missing for some older videos, which then keep the small one
            const string Small = "/mqdefault.jpg";
            if (width > 320 && url.IndexOf("i.ytimg.com/vi/", StringComparison.Ordinal) >= 0 && url.EndsWith(Small, StringComparison.Ordinal))
            {
                try { data = Download(url.Substring(0, url.Length - Small.Length) + "/hq720.jpg"); } catch { data = null; }
            }
            if (data == null || data.Length < 2000) data = Download(url);
            BitmapImage bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.DecodePixelWidth = width;
            bi.StreamSource = new MemoryStream(data);
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
    }

    /// <summary>Ids of videos that count as watched (Nomad history, FreeTube history, any saved position). O(1) lookups for the cards.</summary>
    internal static class WatchedIds
    {
        private static readonly object lk = new object();
        private static HashSet<string> ids = new HashSet<string>();
        private static readonly HashSet<string> added = new HashSet<string>();   // watched during this session (survives Rebuild races)

        public static bool Contains(string id)
        {
            lock (lk) return ids.Contains(id);
        }

        public static void Add(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            lock (lk) { added.Add(id); ids.Add(id); }
        }

        /// <summary>Re-reads the history files. Call off the UI thread.</summary>
        public static void Rebuild(bool forgetSession)
        {
            HashSet<string> fresh = new HashSet<string>();
            try { foreach (VideoItem v in NomadStore.NomadHistory()) fresh.Add(v.Id); } catch { }
            try { foreach (VideoItem v in FreeTubeData.LoadHistory(0)) fresh.Add(v.Id); } catch { }
            try { foreach (string id in NomadStore.PositionIds()) fresh.Add(id); } catch { }
            lock (lk)
            {
                if (forgetSession) added.Clear();
                foreach (string a in added) fresh.Add(a);
                ids = fresh;
            }
        }
    }

    /// <summary>Grid cards (fixed size so scrolling maths stays trivial).</summary>
    internal static class Cards
    {
        // a cell holds the card (6 px ring gap inside it) plus the gap to the next one; the face is ThumbW wide (16:9 thumbnail).
        // Five cells = 1820; with the grid 49 px from the left the faces span x = 64..1854: the home screen's 64 px side margins.
        public const double CellW = 364, CellH = 326, ThumbW = 334, ThumbH = 188, Radius = 12;

        private static Border Shell(out Grid face)
        {
            Border card = Theme.Card(CellW - 18, CellH - 16, Radius, out face);
            card.Margin = new Thickness(9, 8, 9, 8);
            return card;
        }

        private static Brush Shade(double alpha) { return Theme.ScrimA(alpha); }

        private static Border Badge(string text, HorizontalAlignment h)
        {
            Border b = Theme.Chip(text, Shade(0.82), Theme.Ink, 18);
            b.Margin = new Thickness(10, 0, 10, 10);
            b.HorizontalAlignment = h;
            b.VerticalAlignment = VerticalAlignment.Bottom;
            return b;
        }

        // Both helpers measure instead of trusting TextBlock trimming: a wrapped TextBlock does not reliably
        // put its "..." on the last allowed line, which cut every grid title to one line.
        private static double MeasureWidth(string text, double size, FontWeight weight, FontFamily family)
        {
            TextBlock m = new TextBlock { Text = text, FontSize = size, FontFamily = family, FontWeight = weight };
            m.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return m.DesiredSize.Width;
        }

        /// <summary>The longest of the candidates (best first) that fits one line of the given width, else the last one cut with "...".</summary>
        private static string FitLine(double size, double width, params string[] candidates)
        {
            string last = "";
            foreach (string c in candidates)
            {
                if (string.IsNullOrEmpty(c)) continue;
                last = c;
                if (MeasureWidth(c, size, FontWeights.Normal, Theme.Font) <= width) return c;
            }
            return last;
        }

        /// <summary>Cuts text with "..." so it fills at most `lines` wrapped lines of the given width.</summary>
        private static string FitLines(string text, double size, FontWeight weight, FontFamily family, double lineHeight, int lines, double width)
        {
            if (string.IsNullOrEmpty(text)) return "";
            TextBlock m = new TextBlock { FontSize = size, FontFamily = family, FontWeight = weight, TextWrapping = TextWrapping.Wrap, LineHeight = lineHeight, Width = width };
            double limit = lineHeight * lines + 1;
            m.Text = text;
            m.Measure(new Size(width, double.PositiveInfinity));
            if (m.DesiredSize.Height <= limit) return text;
            int lo = 0, hi = text.Length;   // longest prefix that still fits with the ellipsis
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                m.Text = text.Substring(0, mid).TrimEnd() + "…";
                m.Measure(new Size(width, double.PositiveInfinity));
                if (m.DesiredSize.Height <= limit) lo = mid; else hi = mid - 1;
            }
            return text.Substring(0, lo).TrimEnd() + "…";
        }

        private static TextBlock Title(string text, double size, int lines)
        {
            double lh = size + 6;
            TextBlock t = Theme.Text(FitLines(text, size, FontWeights.SemiBold, Theme.Display, lh, lines, ThumbW - 28), size, Theme.Ink);
            t.FontFamily = Theme.Display;
            t.FontWeight = FontWeights.SemiBold;
            t.TextWrapping = TextWrapping.Wrap;
            t.LineHeight = lh;
            t.MinHeight = lh * lines;   // every card's meta line starts at the same height, one-line titles included
            t.MaxHeight = lh * lines;
            t.Margin = new Thickness(14, 10, 14, 0);
            t.VerticalAlignment = VerticalAlignment.Top;
            return t;
        }

        /// <summary>A thin progress bar: the accent fills `frac` of the width, whatever the card's width.</summary>
        private static Grid Progress(double frac)
        {
            frac = Math.Max(0.02, Math.Min(1.0, frac));
            Grid track = new Grid { Height = 5, VerticalAlignment = VerticalAlignment.Bottom, Background = Theme.InkA(0.25), IsHitTestVisible = false };
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(frac, GridUnitType.Star) });
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - frac, GridUnitType.Star) });
            track.Children.Add(new Rectangle { Fill = Theme.Accent });
            return track;
        }

        private static Grid Thumb()
        {
            return new Grid { Width = ThumbW, Height = ThumbH, Background = Theme.InkA(0.06), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Left };
        }

        private static string Join(params string[] parts)
        {
            List<string> keep = new List<string>();
            foreach (string p in parts) if (!string.IsNullOrEmpty(p)) keep.Add(p);
            return string.Join("  \u00B7  ", keep.ToArray());
        }

        /// <summary>Published within the last 24 hours (Published is unix seconds, 0 = unknown).</summary>
        public static bool IsNew(VideoItem v)
        {
            if (v.Published <= 0) return false;
            long age = Fmt.NowUnix() - v.Published;
            return age >= -300 && age < 86400;
        }

        // The one fixed-colour badge: LIVE is red (and its text white) in every theme, as on YouTube itself. It sits on a thumbnail.
        public static readonly Brush LiveRed = MakeSolid(Color.FromRgb(0xE5, 0x3A, 0x3A));
        public static readonly Brush LiveInk = MakeSolid(Color.FromRgb(0xFF, 0xFF, 0xFF));
        private static Brush MakeSolid(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        private static Border Pill(string text, Brush back, Brush fore, HorizontalAlignment h, VerticalAlignment va, Thickness margin)
        {
            Border b = Theme.Chip(text, back, fore, 17);
            ((TextBlock)b.Child).FontWeight = FontWeights.Bold;
            b.Margin = margin;
            b.HorizontalAlignment = h;
            b.VerticalAlignment = va;
            return b;
        }

        public static Border Video(VideoItem v, Dispatcher ui) { return Video(v, ui, true); }

        /// <summary>dimWatched: darken the card when the video is in the watch history (not wanted on the History tab itself).</summary>
        public static Border Video(VideoItem v, Dispatcher ui, bool dimWatched)
        {
            Grid face;
            Border card = Shell(out face);
            StackPanel sp = new StackPanel();
            bool watched = dimWatched && !v.IsLive && WatchedIds.Contains(v.Id);
            Grid thumb = Thumb();
            Image img = new Image { Stretch = Stretch.UniformToFill };
            // Deliberately hardware bilinear scaling: Fant/HighQuality resample in software, which is far too
            // costly for a Viewbox-scaled grid of ~30 thumbnails on 1440p/4K screens while scrolling.
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.LowQuality);
            thumb.Children.Add(img);
            string url = string.IsNullOrEmpty(v.Thumbnail) ? Fmt.Thumb(v.Id) : v.Thumbnail;
            CardArt art = CardArt.Attach(card);
            art.Img = img;
            ThumbCache.Load(url, 640, ui, delegate(BitmapImage b) { img.Source = b; art.Notify(); });
            card.Tag = new ThumbHandle { Img = img, Url = url, Width = 640, Ui = ui, Art = art };
            if (watched) thumb.Children.Add(new Rectangle { Fill = Shade(0.55), IsHitTestVisible = false });
            if (v.IsLive)
                thumb.Children.Add(Pill("LIVE", LiveRed, LiveInk, HorizontalAlignment.Right, VerticalAlignment.Bottom, new Thickness(10, 0, 10, 10)));
            else
            {
                string dur = v.DurationText.Length > 0 ? v.DurationText : Fmt.Duration(v.Duration);
                if (dur.Length > 0) thumb.Children.Add(Badge(dur, HorizontalAlignment.Right));
                if (!watched && IsNew(v))
                    thumb.Children.Add(Pill("NEW", Theme.Accent, Theme.OnAccent, HorizontalAlignment.Left, VerticalAlignment.Top, new Thickness(10, 10, 0, 0)));
            }
            if (v.Duration > 0 && v.WatchProgress > 0)
            {
                // watched so far: a thin bar in the theme's accent along the thumbnail's foot
                thumb.Children.Add(Progress(v.WatchProgress / v.Duration));
            }
            sp.Children.Add(thumb);
            sp.Children.Add(Title(v.Title, 21, 2));
            // "channel · age · views" when it fits, else it drops the views, then the age, never ends mid-word
            TextBlock meta = Theme.Text(FitLine(18, ThumbW - 28, Join(v.Author, v.Age, v.Views), Join(v.Author, v.Age), v.Author), 18, Theme.Ink60);
            meta.TextTrimming = TextTrimming.CharacterEllipsis;
            meta.Margin = new Thickness(14, 5, 14, 0);
            sp.Children.Add(meta);
            if (watched)
            {
                foreach (UIElement el in sp.Children) if (!(el is Grid)) el.Opacity = 0.6;   // title + meta; the thumbnail has its own overlay
            }
            face.Children.Add(sp);
            return card;
        }

        public static Border Playlist(PlaylistItem p, Dispatcher ui)
        {
            Grid face;
            Border card = Shell(out face);
            StackPanel sp = new StackPanel();
            Grid thumb = Thumb();
            Image img = new Image { Stretch = Stretch.UniformToFill };
            // Same as Cards.Video: hardware bilinear scaling keeps scrolling cheap on scaled 1440p/4K grids.
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.LowQuality);
            thumb.Children.Add(img);
            CardArt art = CardArt.Attach(card);
            art.Img = img;
            ThumbCache.Load(p.Thumbnail, 640, ui, delegate(BitmapImage b) { img.Source = b; art.Notify(); });
            // the stack of videos: a side panel over the thumbnail's right third, like a playlist on the TV app
            Border side = new Border
            {
                Width = ThumbW * 0.36, HorizontalAlignment = HorizontalAlignment.Right, Background = Shade(0.72), IsHitTestVisible = false
            };
            StackPanel count = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            TextBlock n = Theme.Title(p.VideoCount.ToString(), 30);
            n.HorizontalAlignment = HorizontalAlignment.Center;
            count.Children.Add(n);
            FrameworkElement icon = Theme.Icon(0xE05F, 30, Theme.Ink);   // playlist_play
            if (icon != null) { icon.Margin = new Thickness(0, 4, 0, 0); count.Children.Add(icon); }
            else
            {
                TextBlock w = Theme.Text(p.VideoCount == 1 ? "video" : "videos", 18, Theme.Ink60);
                w.HorizontalAlignment = HorizontalAlignment.Center;
                count.Children.Add(w);
            }
            side.Child = count;
            thumb.Children.Add(side);
            sp.Children.Add(thumb);
            sp.Children.Add(Title(p.Title, 21, 2));
            TextBlock meta = Theme.Text(p.VideoCount + (p.VideoCount == 1 ? " video" : " videos"), 18, Theme.Ink60);
            meta.Margin = new Thickness(14, 5, 14, 0);
            sp.Children.Add(meta);
            face.Children.Add(sp);
            return card;
        }

        public static Border Channel(ChannelItem c, Dispatcher ui)
        {
            Grid face;
            Border card = Shell(out face);
            StackPanel sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            // the avatar in a soft ring of ink, as Selene draws profile pictures
            Grid avatar = new Grid { Width = 164, Height = 164, Margin = new Thickness(0, 30, 0, 14) };
            Ellipse av = new Ellipse { Fill = Theme.InkA(0.08) };
            CardArt art = CardArt.Attach(card);
            art.Av = av;
            ThumbCache.Load(c.Thumbnail, 170, ui, delegate(BitmapImage b) { av.Fill = new ImageBrush(b) { Stretch = Stretch.UniformToFill }; art.Notify(); });
            avatar.Children.Add(av);
            avatar.Children.Add(new Ellipse { Stroke = Theme.InkA(0.18), StrokeThickness = 2, IsHitTestVisible = false });
            sp.Children.Add(avatar);
            TextBlock name = Theme.Title(c.Name, 24);
            name.HorizontalAlignment = HorizontalAlignment.Center;
            name.TextAlignment = TextAlignment.Center;
            name.MaxWidth = ThumbW - 28;
            sp.Children.Add(name);
            if (!string.IsNullOrEmpty(c.Subscribers))
            {
                TextBlock s = Theme.Text(c.Subscribers, 18, Theme.Ink60);
                s.HorizontalAlignment = HorizontalAlignment.Center;
                s.Margin = new Thickness(0, 4, 0, 0);
                sp.Children.Add(s);
            }
            face.Children.Add(sp);
            return card;
        }

        /// <summary>Compact avatar card for the Channels rail above Search results.</summary>
        public static Border ChannelRail(ChannelItem c, Dispatcher ui, bool subscribed, bool isNew, bool compact = false)
        {
            Grid face;
            Border card = Theme.Card(236, compact ? 138 : 188, 14, out face);
            card.Margin = new Thickness(12, 10, 12, 10);   // room for the focus scale (1.05) and the glow inside the rail's clip
            StackPanel sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            double avSize = compact ? 54 : 76;
            Grid avatar = new Grid { Width = avSize, Height = avSize, Margin = new Thickness(0, 0, 0, compact ? 4 : 8) };
            Ellipse av = new Ellipse { Fill = Theme.InkA(0.08) };
            CardArt art = CardArt.Attach(card);
            art.Av = av;
            ThumbCache.Load(c.Thumbnail, compact ? 66 : 90, ui, delegate(BitmapImage b) { av.Fill = new ImageBrush(b) { Stretch = Stretch.UniformToFill }; art.Notify(); });
            avatar.Children.Add(av);
            avatar.Children.Add(new Ellipse { Stroke = Theme.InkA(0.18), StrokeThickness = 2, IsHitTestVisible = false });
            sp.Children.Add(avatar);
            TextBlock name = Theme.Title(c.Name, compact ? 19 : 20);
            name.HorizontalAlignment = HorizontalAlignment.Center;
            name.TextAlignment = TextAlignment.Center;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.MaxWidth = 200;
            sp.Children.Add(name);
            if (!string.IsNullOrEmpty(c.Subscribers))
            {
                TextBlock s = Theme.Text(c.Subscribers, 17, Theme.Ink60);
                s.HorizontalAlignment = HorizontalAlignment.Center;
                s.TextTrimming = TextTrimming.CharacterEllipsis;
                s.MaxWidth = 200;
                sp.Children.Add(s);
            }
            // on the Subscriptions screen every channel is followed: the label would only repeat itself and fight the NEW badge
            if (subscribed && !compact)
            {
                TextBlock f = Theme.Text("Following", 17, Theme.Accent);
                f.FontWeight = FontWeights.Bold;
                f.HorizontalAlignment = HorizontalAlignment.Center;
                sp.Children.Add(f);
            }
            face.Children.Add(sp);
            if (isNew)
            {
                Border pill = Theme.Chip("NEW", Theme.Accent, Theme.OnAccent, 15);
                pill.HorizontalAlignment = HorizontalAlignment.Right;   // a corner badge, clear of the name and counts
                pill.VerticalAlignment = VerticalAlignment.Top;
                pill.Margin = new Thickness(0, 8, 8, 0);
                Panel.SetZIndex(pill, 2);
                face.Children.Add(pill);
            }
            return card;
        }

        // Continue Watching card: 430 x 156 (four fit the 1920 px screen's 64 px margins), thumbnail inset with rounded corners, text beside it
        public const double ContinueW = 430, ContinueH = 156;

        /// <summary>Compact card for the Continue Watching rail (thumbnail + title + channel + age + resume progress).</summary>
        public static Border ContinueVideo(VideoItem v, Dispatcher ui)
        {
            const double thumbW = 168, thumbH = 95, pad = 14, textW = ContinueW - 12 - pad - thumbW - 16 - 14;
            Grid face;
            Border card = Theme.Card(ContinueW, ContinueH, 12, out face);
            card.Margin = new Thickness(12, 10, 12, 10);   // room for the focus scale (1.05) and the glow inside the rail's clip
            Grid root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid thumb = new Grid
            {
                Width = thumbW, Height = thumbH, Margin = new Thickness(pad, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Background = Theme.InkA(0.06), Clip = new RectangleGeometry(new Rect(0, 0, thumbW, thumbH), 8, 8)
            };
            Image img = new Image { Stretch = Stretch.UniformToFill };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.LowQuality);
            thumb.Children.Add(img);
            string url = string.IsNullOrEmpty(v.Thumbnail) ? Fmt.Thumb(v.Id) : v.Thumbnail;
            CardArt art = CardArt.Attach(card);
            art.Img = img;
            ThumbCache.Load(url, 320, ui, delegate(BitmapImage b) { img.Source = b; art.Notify(); });
            root.Children.Add(thumb);
            StackPanel text = new StackPanel { Margin = new Thickness(16, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
            TextBlock t = Theme.Text(FitLines(v.Title, 20, FontWeights.SemiBold, Theme.Display, 26, 2, textW), 20, Theme.Ink);
            t.FontFamily = Theme.Display;
            t.FontWeight = FontWeights.SemiBold;
            t.TextWrapping = TextWrapping.Wrap;
            t.LineHeight = 26;
            t.MaxHeight = 52;
            text.Children.Add(t);
            // channel and age on their own lines: a joined "channel · age" was cut off after a few letters
            if (!string.IsNullOrEmpty(v.Author))
            {
                TextBlock who = Theme.Text(v.Author, 17, Theme.Ink60);
                who.Margin = new Thickness(0, 6, 0, 0);
                text.Children.Add(who);
            }
            if (!string.IsNullOrEmpty(v.Age)) text.Children.Add(Theme.Text(v.Age, 17, Theme.Ink60));
            Grid.SetColumn(text, 1);
            root.Children.Add(text);
            face.Children.Add(root);
            if (v.Duration > 0 && v.WatchProgress > 0) face.Children.Add(Progress(v.WatchProgress / v.Duration));
            return card;
        }
    }

    /// <summary>Pulsing placeholder cards while an empty Subscriptions feed is loading.</summary>
    internal sealed class SkeletonGrid : Grid
    {
        private readonly WrapPanel panel;
        private bool shown;

        public SkeletonGrid(int cols)
        {
            IsHitTestVisible = false;
            ClipToBounds = true;
            Visibility = Visibility.Collapsed;
            Margin = new Thickness(49, 12, 0, 40);   // as the grid: the cards' faces start at x = 64
            panel = new WrapPanel { Width = cols * Cards.CellW, HorizontalAlignment = HorizontalAlignment.Left };
            for (int i = 0; i < 2 * cols; i++) panel.Children.Add(Card());
            Children.Add(panel);
        }

        private static Border Bar(double w, double h, Thickness margin)
        {
            return new Border
            {
                Width = w, Height = h, Margin = margin, CornerRadius = new CornerRadius(5),
                Background = Theme.InkA(0.09), IsHitTestVisible = false
            };
        }

        private static Border Card()
        {
            Grid face;
            Border card = Theme.Card(Cards.CellW - 18, Cards.CellH - 16, Cards.Radius, out face);
            card.Margin = new Thickness(9, 8, 9, 8);
            StackPanel sp = new StackPanel();
            Border pic = Bar(Cards.ThumbW, Cards.ThumbH, new Thickness(0));
            pic.CornerRadius = new CornerRadius(0);
            sp.Children.Add(pic);
            Border l1 = Bar(Cards.ThumbW - 70, 18, new Thickness(14, 16, 14, 0));
            l1.HorizontalAlignment = HorizontalAlignment.Left;
            sp.Children.Add(l1);
            Border l2 = Bar(Cards.ThumbW - 130, 18, new Thickness(14, 9, 14, 0));
            l2.HorizontalAlignment = HorizontalAlignment.Left;
            sp.Children.Add(l2);
            Border l3 = Bar(Cards.ThumbW - 180, 14, new Thickness(14, 12, 14, 0));
            l3.HorizontalAlignment = HorizontalAlignment.Left;
            sp.Children.Add(l3);
            face.Children.Add(sp);
            return card;
        }

        public void Show()
        {
            if (shown) return;
            shown = true;
            Visibility = Visibility.Visible;
            panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0.45, 0.9, TimeSpan.FromSeconds(1.0))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever
            });
        }

        public void Hide()
        {
            if (!shown) return;
            shown = false;
            panel.BeginAnimation(OpacityProperty, null);
            panel.Opacity = 1;
            Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Modal list (context menu, Start menu, player pickers). Owner routes the pad to Move/Current.</summary>
    internal sealed class PickerPanel : Border
    {
        public sealed class Entry
        {
            public string Label;
            public bool Checked;
            public int Icon;         // Material icon code shown before the label (0 = none; a check mark marks the chosen row of an option list)
            public Func<bool> Run;   // false = ask the host to close the overlay
            public Border Box;
        }

        private readonly TextBlock title = Theme.Title("", 34);
        private readonly StackPanel list = new StackPanel();
        private readonly Border card = new Border();
        private List<Entry> entries = new List<Entry>();
        private int index;

        public bool IsOpen { get { return Visibility == Visibility.Visible; } }
        public Entry Current { get { return index >= 0 && index < entries.Count ? entries[index] : null; } }

        // the whole screen dimmed, a glass card in the middle (Selene's dialogs)
        public PickerPanel()
        {
            Background = Theme.ScrimA(0.62);
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;
            Visibility = Visibility.Collapsed;
            Theme.Glass(card, 28);
            card.Padding = new Thickness(26, 30, 26, 26);
            card.MinWidth = 600;
            card.MaxWidth = 900;
            card.HorizontalAlignment = HorizontalAlignment.Center;
            card.VerticalAlignment = VerticalAlignment.Center;
            StackPanel sp = new StackPanel();
            title.Margin = new Thickness(22, 0, 22, 18);
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            sp.Children.Add(title);
            sp.Children.Add(new ScrollViewer
            {
                MaxHeight = 690, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = list, Padding = new Thickness(8, 6, 8, 6)
            });
            card.Child = sp;
            Child = card;
        }

        public void Open(string heading, List<Entry> items, int start)
        {
            title.Text = heading;
            title.Visibility = string.IsNullOrEmpty(heading) ? Visibility.Collapsed : Visibility.Visible;
            entries = items;
            list.Children.Clear();
            bool anyChecked = false;
            foreach (Entry e in items) anyChecked |= e.Checked;
            foreach (Entry e in items)
            {
                // 60 px pill rows, left aligned. An option list: the chosen one carries a check, the others keep its room so the words line up
                bool checks = e.Icon == 0 && anyChecked;
                e.Box = Theme.Tile(e.Label, 24, e.Icon != 0 ? e.Icon : (checks ? 0xE5CA : 0), Look.Pill);   // 0xE5CA = check
                if (checks) Theme.SetIconShown(e.Box, e.Checked);
                e.Box.Height = 60;
                e.Box.Padding = new Thickness(28, 0, 28, 1);
                e.Box.Margin = new Thickness(4, 3, 4, 3);
                Theme.SetFocusScale(e.Box, 1.02);   // (the list clips its sides)
                Theme.AlignLeft(e.Box);
                Theme.SetSelected(e.Box, e.Checked);
                list.Children.Add(e.Box);
            }
            index = Math.Max(0, Math.Min(start, items.Count - 1));
            bool wasOpen = Visibility == Visibility.Visible;
            Visibility = Visibility.Visible;
            if (!wasOpen) YtFx.FadeIn(card, 180);
            Paint();
        }

        public void Close() { Visibility = Visibility.Collapsed; }

        public void Move(int d)
        {
            if (entries.Count == 0) return;
            index = (index + d + entries.Count) % entries.Count;
            Paint();
        }

        private void Paint()
        {
            for (int i = 0; i < entries.Count; i++) Theme.SetFocused(entries[i].Box, i == index);
            Border b = Current != null ? Current.Box : null;
            if (b != null) Dispatcher.BeginInvoke(new Action(delegate { b.BringIntoView(); }), DispatcherPriority.Loaded);
        }
    }

    /// <summary>Dotted ring that rotates while visible.</summary>
    internal sealed class Spinner : Grid
    {
        public Spinner(double size)
        {
            Width = size; Height = size;
            RotateTransform rot = new RotateTransform();
            Children.Add(new Ellipse
            {
                Stroke = Theme.Accent, StrokeThickness = size / 9,
                StrokeDashArray = new DoubleCollection(new double[] { 3.2, 1.6 }),
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = rot
            });
            DoubleAnimation a = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever };
            rot.BeginAnimation(RotateTransform.AngleProperty, a);
            IsHitTestVisible = false;
        }
    }

    /// <summary>Eases a ScrollViewer towards a target offset, one step per frame (render loop or paced timer).</summary>
    internal sealed class SmoothScroller
    {
        // A CompositionTarget.Rendering handler makes WPF present at the FASTEST monitor's rate on a multi-monitor PC.
        // On that fastest screen (also a single-monitor PC) the render loop's rate IS the connected display's rate, so we
        // ride it for full 120/144/180 Hz scrolling. On any slower screen we must never use that loop: a 60 Hz TV beside
        // a 180 Hz monitor would be over-drawn 180 times a second. There we use a DispatcherTimer paced to THIS
        // display's rate instead. One source at a time: Stop() always detaches both so they can never double up.
        private enum Clock { None, Rendering, Timer }
        private const double EaseK = 16.0;    // time constant: ~300 ms for a one-row (326 px) scroll
        private const double MaxDt = 0.05;    // ignore long pauses (hidden window, GC, monitor switch)

        private Clock active = Clock.None;
        private bool renderingHooked, timerRes;
        private readonly ScrollViewer sv;
        // Input (5) runs before the Render pass and no longer outranks real input. The timer is now only
        // a paced source of ticks; time-based easing absorbs any jitter that a lower priority introduces.
        private readonly DispatcherTimer timer = new DispatcherTimer(DispatcherPriority.Input);
        /// <summary>Height at the bottom of the viewport that something else covers (the button hints): Reveal keeps rows above it.</summary>
        public double BottomInset;
        private double target;
        private double lastStamp = -1.0;      // milliseconds, reset whenever the clock source changes
        private int idle;

        public SmoothScroller(ScrollViewer scroll)
        {
            sv = scroll;
            timer.Tick += OnFrame;
        }

        public void Snap(double offset)
        {
            Stop();
            target = Math.Max(0, offset);
            sv.ScrollToVerticalOffset(target);
        }

        public void To(double offset)
        {
            target = Math.Max(0, offset);
            idle = 0;

            Clock want = ScreenClock.IsFastest(sv) ? Clock.Rendering : Clock.Timer;
            bool running = want == Clock.Timer ? timer.IsEnabled : renderingHooked;
            if (want != active || !running)
            {
                Stop();                       // detach/stop ANY source: leaves active = None, never two clocks
                lastStamp = -1.0;             // new source => new time baseline for dt
                if (want == Clock.Rendering)
                {
                    // fastest screen: the render loop already ticks at this display's rate, scroll at full speed
                    CompositionTarget.Rendering += OnFrame;
                    renderingHooked = true;
                }
                else
                {
                    // slower screen in a multi-monitor layout: pace to this display (30..240 Hz clamp).
                    // 0 from ScreenClock means "unknown" (never a cached fake 60); use a last-resort
                    // 60 for the interval only, without pretending it was a measurement.
                    int rate = ScreenClock.Rate(sv);
                    if (rate <= 0) rate = 60;
                    timer.Interval = TimeSpan.FromMilliseconds(1000.0 / Math.Max(30, Math.Min(240, rate)));
                    timer.Start();
                    if (!timerRes) { timerRes = true; TimerRes.Acquire(); }   // the 16.7 ms tick needs the 1 ms timer resolution
                }
                active = want;
            }
        }

        public void Stop()
        {
            if (renderingHooked) { CompositionTarget.Rendering -= OnFrame; renderingHooked = false; }
            if (timer.IsEnabled) timer.Stop();
            if (timerRes) { timerRes = false; TimerRes.Release(); }
            active = Clock.None;
            lastStamp = -1.0;
        }

        /// <summary>Scroll target so that the row [top, top+h] is fully visible with a small margin.</summary>
        public double Reveal(double top, double h)
        {
            double view = sv.ActualHeight - BottomInset, off = target;
            if (view < 10) return off;
            if (top - 8 < off) off = top - 8;
            else if (top + h + 8 > off + view) off = top + h + 8 - view;
            return Math.Max(0, off);
        }

        // Rendering ticks expose their own timestamp; the paced timer does not, so fall back to UTC.
        private static double FrameStamp(EventArgs e)
        {
            RenderingEventArgs re = e as RenderingEventArgs;
            if (re != null)
            {
                double ms = re.RenderingTime.TotalMilliseconds;
                if (ms > 0.0) return ms;
            }
            return System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency;   // UtcNow moves in 15.6 ms steps
        }

        private void OnFrame(object s, EventArgs e)
        {
            double now = FrameStamp(e);
            double dt = lastStamp < 0 ? 1.0 / 60.0 : (now - lastStamp) / 1000.0;
            lastStamp = now;
            if (!(dt > 0.0)) dt = 1.0 / 60.0;   // duplicate/zero timestamp: keep a sane step
            if (dt > MaxDt) dt = MaxDt;          // do not jump after a long pause

            double goal = Math.Min(target, Math.Max(0, sv.ScrollableHeight));
            double cur = sv.VerticalOffset, d = goal - cur;
            if (Math.Abs(d) < 1.0)               // unchanged snap threshold
            {
                sv.ScrollToVerticalOffset(goal);
                idle++;                          // always count: the old || short-circuit skipped this
                // Target past the end of the content: keep pinning to the end for a bounded number
                // of ticks. A reachable target stops on this very frame, as before.
                if (target <= sv.ScrollableHeight + 1 || idle > 60) Stop();
                return;
            }
            idle = 0;
            // Time-based easing: identical ~250 ms duration at 74 Hz and 180 Hz, and irregular
            // timer ticks no longer change how far each step moves.
            double factor = 1.0 - Math.Exp(-EaseK * dt);
            sv.ScrollToVerticalOffset(cur + d * factor);
        }
    }

    /// <summary>Small bottom-center message that disappears by itself.</summary>
    internal sealed class Toaster : Border
    {
        private readonly TextBlock text = Theme.Text("", 23, Theme.Ink);
        private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2600) };

        // Selene's notice: a glass pill, bottom centre, above the button hints
        public Toaster()
        {
            Theme.Glass(this, 28);
            Padding = new Thickness(34, 12, 34, 14);
            Margin = new Thickness(0, 0, 0, 112);
            HorizontalAlignment = HorizontalAlignment.Center;
            VerticalAlignment = VerticalAlignment.Bottom;
            Visibility = Visibility.Collapsed;
            IsHitTestVisible = false;
            Child = text;
            SizeChanged += delegate { CornerRadius = new CornerRadius(Math.Max(0, ActualHeight / 2)); };   // a capsule
            timer.Tick += delegate { timer.Stop(); Visibility = Visibility.Collapsed; };
        }

        public void Show(string message)
        {
            text.Text = message;
            bool wasShown = Visibility == Visibility.Visible;
            Visibility = Visibility.Visible;
            if (!wasShown) YtFx.FadeIn(this, 180);
            timer.Stop();
            timer.Start();
        }

        public void Stop() { timer.Stop(); }
    }
}
