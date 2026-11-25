// CardInfo.cs - the numbers on the home screen's Browser and YouTube cards (bookmarks, last page, subscriptions...).
// The theme's info panels for these two cards read plain string resources (SeleneBrowserStat*, SeleneYouTubeStat*); they
// are filled here, off the UI thread, when the add-on starts, when either screen is closed and when a browser setting changes.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using Playnite.SDK;

namespace SeleneBrowser
{
    internal static class CardInfo
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly object gate = new object();
        private static bool hooked, running, again;

        public static void Refresh()
        {
            lock (gate)
            {
                if (!hooked) { hooked = true; BrowserSettings.Changed += Refresh; }
                if (running) { again = true; return; }   // (one at a time; a request meanwhile runs once more)
                running = true;
            }
            Task.Run(delegate
            {
                while (true)
                {
                    try
                    {
                        SyncProfile();
                        var ui = Application.Current == null ? null : Application.Current.Dispatcher;
                        foreach (var kv in Collect()) SetResource(kv.Key, kv.Value, ui);
                        PublishFeeds(ui);
                    }
                    catch (Exception ex) { logger.Warn("SeleneBrowser: card info: " + ex.Message); }
                    lock (gate) { if (!again) { running = false; return; } again = false; }
                }
            });
        }

        private static Dictionary<string, string> Collect()
        {
            var v = new Dictionary<string, string>();

            // Browser: bookmarks and history are the add-on's own JSON lists
            string dir = SeleneBrowserPlugin.DataDir;
            List<BrowserEntry> marks = ReadList(Path.Combine(dir, "bookmarks.json"));
            List<BrowserEntry> pages = ReadList(Path.Combine(dir, "history.json"));
            v["SeleneBrowserStatBookmarks"] = marks == null ? "Starter set" : marks.Count == 0 ? "None yet" : marks.Count.ToString();
            BrowserEntry last = pages != null && pages.Count > 0 ? pages[0] : null;
            v["SeleneBrowserStatLast"] = last == null ? "Nothing yet" : Short(PageName(last), 34);
            v["SeleneBrowserStatAds"] = BrowserSettings.AdBlockEnabled ? "On" : "Off";

            // YouTube: channels followed and what was watched (Lanternfall's own history merged with FreeTube's)
            int subs = 0;
            try { subs = Subscriptions.Channels().Count; } catch (Exception ex) { logger.Warn("SeleneBrowser: card info subscriptions: " + ex.Message); }
            List<VideoItem> seen = null;
            try { seen = NomadStore.MergedHistory(0); } catch (Exception ex) { logger.Warn("SeleneBrowser: card info history: " + ex.Message); }
            v["SeleneYouTubeStatSubs"] = subs == 0 ? "None yet" : subs == 1 ? "1 channel" : subs + " channels";
            int fresh = 0;
            try { fresh = NewVideos(); } catch (Exception ex) { logger.Warn("SeleneBrowser: card info new videos: " + ex.Message); }
            v["SeleneYouTubeStatNew"] = subs == 0 ? "-" : fresh == 0 ? "None" : fresh >= 50 ? "50+" : fresh.ToString();
            // the newest watched video that has a name (an entry saved without one only has its id)
            VideoItem named = seen == null ? null : seen.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Title) && x.Title != x.Id);
            v["SeleneYouTubeStatLast"] = named == null ? "Nothing yet" : Short(named.Title, 34);
            return v;
        }

        // Videos of your channels published since YouTube was last closed (the feed as last fetched). The mark moves each time the
        // YouTube screen is left; the first time there is no mark yet, so nothing counts as new.
        private static string SeenFile { get { return NomadStore.ScopedFile("yt-card-seen.txt"); } }

        // The home cards and the background feed watcher live outside the YouTube overlay, so they must
        // notice a "Who's playing?" switch themselves instead of waiting for the YouTube screen to open.
        private static void SyncProfile()
        {
            try { if (ProfileScope.Refresh()) NomadStore.ReloadForProfile(); } catch { }
        }

        public static void MarkYouTubeSeen()
        {
            try { File.WriteAllText(SeenFile, Fmt.NowUnix().ToString()); } catch { }
        }

        private static long SeenMark()
        {
            long mark;
            if (!File.Exists(SeenFile) || !long.TryParse(File.ReadAllText(SeenFile).Trim(), out mark)) { MarkYouTubeSeen(); return 0; }
            return mark;
        }

        private static int NewVideos()
        {
            long mark = SeenMark();
            return mark == 0 ? 0 : Subscriptions.LoadCached().Count(x => x.Published > mark);
        }

        // The three tiles under each card. Shaping is in CardFeed (pure), drawing in CardFeedArt (no UI-thread I/O).
        // SetResource goes straight to Application.Resources, so a thumbnail arriving later still reaches the theme.
        private static void PublishFeeds(System.Windows.Threading.Dispatcher ui)
        {
            Action<string, object> set = delegate(string key, object value) { SetResource(key, value, ui); };
            List<VideoItem> feed = null;
            try { feed = Subscriptions.LoadCached(); } catch (Exception ex) { logger.Warn("SeleneBrowser: card feed tiles: " + ex.Message); }
            bool shorts = NomadStore.GetPref("yt.shorts", "1") != "0";   // the same switch the YouTube screen uses
            CardFeedArt.Write(set, CardFeed.YouTube(feed, SeenMark(), CardFeed.Tiles, shorts), "SeleneYouTubeFeed", ui);
            string dir = SeleneBrowserPlugin.DataDir;
            List<BrowserEntry> marks = ReadList(Path.Combine(dir, "bookmarks.json"));
            if (marks == null) marks = BrowserControl.DefaultBookmarks();   // no file yet: the same starter tiles the browser shows
            CardFeedArt.Write(set, CardFeed.Browser(marks,
                ReadList(Path.Combine(dir, "history.json")), CardFeed.Tiles), "SeleneBrowserFeed", ui);
        }

        // Fetches the feed in the background (the same update the YouTube screen does when opened), so the card can say there is
        // something new without opening it: a minute after start and then every half hour, only when the last fetch is older than that.
        private static System.Threading.Timer feedTimer, profileTimer;
        public static void WatchFeed()
        {
            if (feedTimer != null) return;
            // who signed in (or switched in "Who's playing?") republishes the cards at once, not on the next 30-minute tick
            profileTimer = new System.Threading.Timer(delegate
            {
                try { if (ProfileScope.Refresh()) { NomadStore.ReloadForProfile(); Refresh(); } }
                catch (Exception ex) { logger.Warn("SeleneBrowser: profile watch: " + ex.Message); }
            }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
            feedTimer = new System.Threading.Timer(delegate
            {
                try
                {
                    SyncProfile();
                    if (Subscriptions.IsRefreshing) return;
                    // not while a game has the screen (Playnite is behind it): three feed fetches and thumbnails would cost its frames
                    bool inFront = true;
                    try
                    {
                        var app = Application.Current;
                        if (app != null) app.Dispatcher.Invoke(new Action(delegate { var w = app.MainWindow; inFront = w != null && w.IsActive; }));
                    }
                    catch { }
                    if (!inFront) return;
                    if (Fmt.NowUnix() - NomadStore.LoadFeed().LastRefresh < 25 * 60) return;
                    if (Subscriptions.Channels().Count == 0) return;
                    Subscriptions.Refresh(null).ContinueWith(delegate { Refresh(); });
                }
                catch (Exception ex) { logger.Warn("SeleneBrowser: card feed: " + ex.Message); }
            }, null, TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(30));
        }

        // Always marshal writes to the UI thread: ThumbCache can call back synchronously when a picture is already cached,
        // which would otherwise write to the resource dictionary from the refresh worker.
        private static void SetResource(string key, object value, System.Windows.Threading.Dispatcher ui)
        {
            if (ui == null || ui.CheckAccess()) { if (Application.Current != null) Application.Current.Resources[key] = value; return; }
            ui.BeginInvoke(new Action(delegate
            {
                if (Application.Current != null) Application.Current.Resources[key] = value;
            }));
        }

        // null = no file (never used), an empty list = used and emptied
        private static List<BrowserEntry> ReadList(string file)
        {
            try
            {
                if (!File.Exists(file)) return null;
                return new JavaScriptSerializer().Deserialize<List<BrowserEntry>>(File.ReadAllText(file, Encoding.UTF8)) ?? new List<BrowserEntry>();
            }
            catch { return null; }
        }

        // the page's title, or its host when the title is just the address
        private static string PageName(BrowserEntry e)
        {
            string title = e.Title;
            if (!string.IsNullOrWhiteSpace(title) && !title.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return title.Trim();
            Uri u;
            if (Uri.TryCreate(e.Url ?? title ?? "", UriKind.Absolute, out u) && !string.IsNullOrEmpty(u.Host)) return u.Host.StartsWith("www.") ? u.Host.Substring(4) : u.Host;
            return (title ?? e.Url ?? "").Trim();
        }

        private static string Short(string s, int max)
        {
            s = (s ?? "").Trim();
            return s.Length <= max ? s : s.Substring(0, max - 1).TrimEnd() + "…";
        }
    }
}
