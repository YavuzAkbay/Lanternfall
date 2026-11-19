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
                    if (cache.Count > 160) cache.Clear();   // 360 px thumbs are ~0.3 MB decoded
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

        private static BitmapImage Fetch(string url, int width)
        {
            byte[] data;
            HttpWebRequest r = Http.Create("GET", url, 10000, null);
            using (WebResponse resp = r.GetResponse())
            using (Stream s = resp.GetResponseStream())
            using (MemoryStream ms = new MemoryStream())
            {
                s.CopyTo(ms);
                data = ms.ToArray();
            }
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
        // a cell holds the card (6 px ring gap inside it) plus the gap to the next one; the face is ThumbW wide
        public const double CellW = 382, CellH = 326, ThumbW = 352, ThumbH = 198, Radius = 12;

        private static Border Shell(out Grid face)
        {
            Border card = Theme.Card(CellW - 18, CellH - 16, Radius, out face);
            card.Margin = new Thickness(9, 8, 9, 8);
            return card;
        }

        private static Brush Shade(double alpha) { return Theme.ScrimA(alpha); }

        private static Border Badge(string text, HorizontalAlignment h)
        {
            Border b = Theme.Chip(text, Shade(0.82), Theme.Ink, 17);
            b.Margin = new Thickness(10, 0, 10, 10);
            b.HorizontalAlignment = h;
            b.VerticalAlignment = VerticalAlignment.Bottom;
            return b;
        }

        private static TextBlock Title(string text, double size, int lines)
        {
            TextBlock t = Theme.Text(text, size, Theme.Ink);
            t.FontFamily = Theme.Display;
            t.FontWeight = FontWeights.SemiBold;
            t.TextWrapping = TextWrapping.Wrap;
            t.LineHeight = size + 6;
            t.MaxHeight = (size + 6) * lines;
            t.Margin = new Thickness(14, 10, 14, 0);
            t.VerticalAlignment = VerticalAlignment.Top;
            return t;
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

        public static readonly Brush LiveRed = MakeLiveRed();
        private static Brush MakeLiveRed() { var b = new SolidColorBrush(Color.FromRgb(0xE5, 0x3A, 0x3A)); b.Freeze(); return b; }

        private static Border Pill(string text, Brush back, Brush fore, HorizontalAlignment h, VerticalAlignment va, Thickness margin)
        {
            Border b = Theme.Chip(text, back, fore, 16);
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
            thumb.Children.Add(img);
            string url = string.IsNullOrEmpty(v.Thumbnail) ? Fmt.Thumb(v.Id) : v.Thumbnail;
            ThumbCache.Load(url, 360, ui, delegate(BitmapImage b) { img.Source = b; });
            if (watched) thumb.Children.Add(new Rectangle { Fill = Shade(0.55), IsHitTestVisible = false });
            if (v.IsLive)
                thumb.Children.Add(Pill("LIVE", LiveRed, Brushes.White, HorizontalAlignment.Right, VerticalAlignment.Bottom, new Thickness(10, 0, 10, 10)));
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
                double frac = Math.Min(1.0, v.WatchProgress / v.Duration);
                Grid track = new Grid { Height = 5, VerticalAlignment = VerticalAlignment.Bottom, Background = Theme.InkA(0.25) };
                track.Children.Add(new Rectangle { Width = ThumbW * frac, Fill = Theme.Accent, HorizontalAlignment = HorizontalAlignment.Left });
                thumb.Children.Add(track);
            }
            sp.Children.Add(thumb);
            sp.Children.Add(Title(v.Title, 21, 2));
            TextBlock meta = Theme.Text(Join(v.Author, v.Age, v.Views), 18, Theme.Ink60);
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
            thumb.Children.Add(img);
            ThumbCache.Load(p.Thumbnail, 360, ui, delegate(BitmapImage b) { img.Source = b; });
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
                TextBlock w = Theme.Text(p.VideoCount == 1 ? "video" : "videos", 17, Theme.Ink60);
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
            ThumbCache.Load(c.Thumbnail, 170, ui, delegate(BitmapImage b) { av.Fill = new ImageBrush(b) { Stretch = Stretch.UniformToFill }; });
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
    }

    /// <summary>Modal list (context menu, Start menu, player pickers). Owner routes the pad to Move/Current.</summary>
    internal sealed class PickerPanel : Border
    {
        public sealed class Entry
        {
            public string Label;
            public bool Checked;
            public Func<bool> Run;   // false = ask the host to close the overlay
            public Border Box;
        }

        private readonly TextBlock title = Theme.Title("", 30);
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
            card.Padding = new Thickness(26, 28, 26, 26);
            card.MinWidth = 520;
            card.MaxWidth = 900;
            card.HorizontalAlignment = HorizontalAlignment.Center;
            card.VerticalAlignment = VerticalAlignment.Center;
            StackPanel sp = new StackPanel();
            title.Margin = new Thickness(22, 0, 22, 16);
            sp.Children.Add(title);
            sp.Children.Add(new ScrollViewer
            {
                MaxHeight = 640, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = list, Padding = new Thickness(8, 4, 8, 4)
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
                // the chosen option carries a check; the others keep its room so the words line up
                e.Box = Theme.Tile(e.Label, 26, anyChecked ? 0xE5CA : 0, Look.Ghost);   // check
                Theme.SetIconShown(e.Box, e.Checked);
                e.Box.Padding = new Thickness(26, 11, 26, 12);
                e.Box.Margin = new Thickness(6, 3, 6, 3);
                Theme.SetFocusScale(e.Box, 1.02);   // (the list clips its sides)
                Theme.AlignLeft(e.Box);
                Theme.SetSelected(e.Box, e.Checked);
                list.Children.Add(e.Box);
            }
            index = Math.Max(0, Math.Min(start, items.Count - 1));
            Visibility = Visibility.Visible;
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

    /// <summary>Eases a ScrollViewer towards a target offset, one step per rendered frame.</summary>
    internal sealed class SmoothScroller
    {
        private readonly ScrollViewer sv;
        private double target;
        private int idle;
        private bool hooked;

        public SmoothScroller(ScrollViewer scroll) { sv = scroll; }

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
            if (hooked) return;
            hooked = true;
            CompositionTarget.Rendering += OnFrame;
        }

        public void Stop()
        {
            if (!hooked) return;
            hooked = false;
            CompositionTarget.Rendering -= OnFrame;
        }

        /// <summary>Scroll target so that the row [top, top+h] is fully visible with a small margin.</summary>
        public double Reveal(double top, double h)
        {
            double view = sv.ActualHeight, off = target;
            if (view < 10) return off;
            if (top - 8 < off) off = top - 8;
            else if (top + h + 8 > off + view) off = top + h + 8 - view;
            return Math.Max(0, off);
        }

        private void OnFrame(object s, EventArgs e)
        {
            double goal = Math.Min(target, Math.Max(0, sv.ScrollableHeight));
            double cur = sv.VerticalOffset, d = goal - cur;
            if (Math.Abs(d) < 1.0)
            {
                sv.ScrollToVerticalOffset(goal);
                if (target <= sv.ScrollableHeight + 1 || ++idle > 60) Stop();
                return;
            }
            idle = 0;
            sv.ScrollToVerticalOffset(cur + d * 0.22);
        }
    }

    /// <summary>Small bottom-center message that disappears by itself.</summary>
    internal sealed class Toaster : Border
    {
        private readonly TextBlock text = Theme.Text("", 22, Theme.Ink);
        private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2600) };

        // Selene's notice: a glass pill
        public Toaster()
        {
            Theme.Glass(this, 26);
            Padding = new Thickness(30, 12, 30, 14);
            Margin = new Thickness(0, 0, 0, 90);
            HorizontalAlignment = HorizontalAlignment.Center;
            VerticalAlignment = VerticalAlignment.Bottom;
            Visibility = Visibility.Collapsed;
            IsHitTestVisible = false;
            Child = text;
            timer.Tick += delegate { timer.Stop(); Visibility = Visibility.Collapsed; };
        }

        public void Show(string message)
        {
            text.Text = message;
            Visibility = Visibility.Visible;
            timer.Stop();
            timer.Start();
        }

        public void Stop() { timer.Stop(); }
    }
}
