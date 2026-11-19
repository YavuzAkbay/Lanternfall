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
                    if (cache.Count > 500) cache.Clear();
                    cache[key] = bmp;
                    foreach (Action<BitmapImage> cb in cbs) { try { cb(bmp); } catch { } }
                }));
            });
        }

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
        public const double CellW = 382, CellH = 326, ThumbW = 358, ThumbH = 201;

        private static Border Shell()
        {
            return new Border
            {
                Width = CellW - 18, Height = CellH - 16, Margin = new Thickness(9, 8, 9, 8),
                BorderThickness = new Thickness(3), BorderBrush = Brushes.Transparent,
                CornerRadius = new CornerRadius(10), Background = Theme.Panel
            };
        }

        private static Brush Shade(byte alpha) { return new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0)); }

        private static Border Badge(string text, HorizontalAlignment h)
        {
            TextBlock t = Theme.Text(text, 18, Brushes.White);
            return new Border
            {
                Background = Shade(0xD9), CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(8, 0, 8, 12), HorizontalAlignment = h, VerticalAlignment = VerticalAlignment.Bottom, Child = t
            };
        }

        private static TextBlock Title(string text, double size, int lines)
        {
            TextBlock t = Theme.Text(text, size, Theme.Ink);
            t.TextWrapping = TextWrapping.Wrap;
            t.LineHeight = size + 6;
            t.MaxHeight = (size + 6) * lines;
            t.Margin = new Thickness(8, 8, 8, 0);
            t.VerticalAlignment = VerticalAlignment.Top;
            return t;
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

        private static Border Pill(string text, Brush back, Brush fore, HorizontalAlignment h, VerticalAlignment va, Thickness margin)
        {
            TextBlock t = Theme.Text(text, 18, fore);
            t.FontWeight = FontWeights.Bold;
            return new Border
            {
                Background = back, CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 2, 8, 2),
                Margin = margin, HorizontalAlignment = h, VerticalAlignment = va, Child = t, IsHitTestVisible = false
            };
        }

        public static Border Video(VideoItem v, Dispatcher ui) { return Video(v, ui, true); }

        /// <summary>dimWatched: darken the card when the video is in the watch history (not wanted on the History tab itself).</summary>
        public static Border Video(VideoItem v, Dispatcher ui, bool dimWatched)
        {
            Border card = Shell();
            StackPanel sp = new StackPanel();
            bool watched = dimWatched && !v.IsLive && WatchedIds.Contains(v.Id);
            Grid thumb = new Grid { Width = ThumbW, Height = ThumbH, Background = Theme.Key, ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Left };
            Image img = new Image { Stretch = Stretch.UniformToFill };
            thumb.Children.Add(img);
            string url = string.IsNullOrEmpty(v.Thumbnail) ? Fmt.Thumb(v.Id) : v.Thumbnail;
            ThumbCache.Load(url, 360, ui, delegate(BitmapImage b) { img.Source = b; });
            if (watched) thumb.Children.Add(new Rectangle { Fill = Shade(0x8C), IsHitTestVisible = false });   // 55% black
            if (v.IsLive)
                thumb.Children.Add(Pill("LIVE", new SolidColorBrush(Color.FromRgb(0xE6, 0x1E, 0x1E)), Brushes.White, HorizontalAlignment.Right, VerticalAlignment.Bottom, new Thickness(8, 0, 8, 12)));
            else
            {
                string dur = v.DurationText.Length > 0 ? v.DurationText : Fmt.Duration(v.Duration);
                if (dur.Length > 0) thumb.Children.Add(Badge(dur, HorizontalAlignment.Right));
                if (!watched && IsNew(v))
                    thumb.Children.Add(Pill("NEW", Theme.Accent, Theme.Background, HorizontalAlignment.Left, VerticalAlignment.Top, new Thickness(8, 8, 0, 0)));
            }
            if (v.Duration > 0 && v.WatchProgress > 0)
            {
                double frac = Math.Min(1.0, v.WatchProgress / v.Duration);
                Grid track = new Grid { Height = 6, VerticalAlignment = VerticalAlignment.Bottom, Background = Shade(0x99) };
                track.Children.Add(new Rectangle { Width = ThumbW * frac, Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x30, 0x30)), HorizontalAlignment = HorizontalAlignment.Left });
                thumb.Children.Add(track);
            }
            sp.Children.Add(thumb);
            sp.Children.Add(Title(v.Title, 22, 2));
            TextBlock meta = Theme.Text(Join(v.Author, v.Age, v.Views), 20, Theme.Ink60);
            meta.Margin = new Thickness(8, 4, 8, 0);
            sp.Children.Add(meta);
            if (watched)
            {
                foreach (UIElement el in sp.Children) if (!(el is Grid)) el.Opacity = 0.6;   // title + meta; the thumbnail has its own overlay
            }
            card.Child = sp;
            return card;
        }

        public static Border Playlist(PlaylistItem p, Dispatcher ui)
        {
            Border card = Shell();
            StackPanel sp = new StackPanel();
            Grid thumb = new Grid { Width = ThumbW, Height = ThumbH, Background = Theme.Key, ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Left };
            Image img = new Image { Stretch = Stretch.UniformToFill };
            thumb.Children.Add(img);
            ThumbCache.Load(p.Thumbnail, 360, ui, delegate(BitmapImage b) { img.Source = b; });
            thumb.Children.Add(Badge(p.VideoCount + (p.VideoCount == 1 ? " video" : " videos"), HorizontalAlignment.Right));
            sp.Children.Add(thumb);
            sp.Children.Add(Title(p.Title, 22, 2));
            card.Child = sp;
            return card;
        }

        public static Border Channel(ChannelItem c, Dispatcher ui)
        {
            Border card = Shell();
            StackPanel sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            Ellipse av = new Ellipse { Width = 170, Height = 170, Fill = Theme.Key, Margin = new Thickness(0, 28, 0, 8) };
            ThumbCache.Load(c.Thumbnail, 170, ui, delegate(BitmapImage b) { av.Fill = new ImageBrush(b) { Stretch = Stretch.UniformToFill }; });
            sp.Children.Add(av);
            TextBlock name = Theme.Text(c.Name, 26, Theme.Ink);
            name.HorizontalAlignment = HorizontalAlignment.Center;
            name.TextAlignment = TextAlignment.Center;
            sp.Children.Add(name);
            if (!string.IsNullOrEmpty(c.Subscribers))
            {
                TextBlock s = Theme.Text(c.Subscribers, 20, Theme.Ink60);
                s.HorizontalAlignment = HorizontalAlignment.Center;
                sp.Children.Add(s);
            }
            card.Child = sp;
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

        private readonly TextBlock title = Theme.Text("", 28, Theme.Ink60);
        private readonly StackPanel list = new StackPanel();
        private List<Entry> entries = new List<Entry>();
        private int index;

        public bool IsOpen { get { return Visibility == Visibility.Visible; } }
        public Entry Current { get { return index >= 0 && index < entries.Count ? entries[index] : null; } }

        public PickerPanel()
        {
            Background = Theme.Panel;
            CornerRadius = new CornerRadius(14);
            Padding = new Thickness(22);
            MinWidth = 460;
            HorizontalAlignment = HorizontalAlignment.Center;
            VerticalAlignment = VerticalAlignment.Center;
            Visibility = Visibility.Collapsed;
            StackPanel sp = new StackPanel();
            title.Margin = new Thickness(6, 0, 0, 10);
            sp.Children.Add(title);
            sp.Children.Add(new ScrollViewer
            {
                MaxHeight = 620, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = list
            });
            Child = sp;
        }

        public void Open(string heading, List<Entry> items, int start)
        {
            title.Text = heading;
            entries = items;
            list.Children.Clear();
            foreach (Entry e in items)
            {
                e.Box = Theme.Tile((e.Checked ? "\u2713  " : "") + e.Label, 28);
                e.Box.Padding = new Thickness(24, 10, 24, 10);
                ((TextBlock)e.Box.Child).HorizontalAlignment = HorizontalAlignment.Left;
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
        private readonly TextBlock text = Theme.Text("", 26, Theme.Ink);
        private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2600) };

        public Toaster()
        {
            Background = Theme.Panel;
            CornerRadius = new CornerRadius(10);
            Padding = new Thickness(26, 12, 26, 12);
            Margin = new Thickness(0, 0, 0, 70);
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
