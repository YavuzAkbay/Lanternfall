// FreeTube NeDB importer (read-only) + Nomad's own small JSON store. C# 5 only.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace SeleneBrowser
{
    public static class FreeTubeData
    {
        public static string Dir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FreeTube"); }
        }

        public static bool IsAvailable { get { return Directory.Exists(Dir) && File.Exists(Path.Combine(Dir, "profiles.db")); } }

        /// <summary>NeDB: one JSON object per line, last line per _id wins, {"$$deleted":true,"_id":..} removes. Order = first appearance of the surviving id.</summary>
        public static List<Dictionary<string, object>> ReadNeDb(string fileName)
        {
            List<Dictionary<string, object>> res = new List<Dictionary<string, object>>();
            try
            {
                string path = Path.Combine(Dir, fileName);
                if (!File.Exists(path)) return res;
                string text;
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader sr = new StreamReader(fs, new UTF8Encoding(false)))
                    text = sr.ReadToEnd();
                Dictionary<string, Dictionary<string, object>> byId = new Dictionary<string, Dictionary<string, object>>();
                List<string> order = new List<string>();
                int autoId = 0;
                foreach (string raw in text.Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.Length < 2 || line[0] != '{') continue;
                    Dictionary<string, object> o;
                    try { o = Jx.Parse(line) as Dictionary<string, object>; } catch { continue; }
                    if (o == null) continue;
                    string id = Jx.Str(o, "_id");
                    if (id.Length == 0) id = "#auto" + (autoId++);
                    if (o.ContainsKey("$$deleted"))
                    {
                        if (byId.Remove(id)) order.Remove(id);
                        continue;
                    }
                    if (!byId.ContainsKey(id)) order.Add(id);
                    byId[id] = o;
                }
                foreach (string id in order) res.Add(byId[id]);
            }
            catch { }
            return res;
        }

        // ------------------------------------------------------------ subscriptions
        public static List<ChannelItem> LoadSubscriptions()
        {
            List<ChannelItem> res = new List<ChannelItem>();
            HashSet<string> seen = new HashSet<string>();
            try
            {
                List<Dictionary<string, object>> profiles = ReadNeDb("profiles.db");
                Dictionary<string, object> all = null;
                foreach (Dictionary<string, object> p in profiles) if (Jx.Str(p, "_id") == "allChannels") all = p;
                List<Dictionary<string, object>> use = new List<Dictionary<string, object>>();
                if (all != null) use.Add(all); else use = profiles;
                foreach (Dictionary<string, object> p in use)
                    foreach (object s in Jx.L(Jx.Get(p, "subscriptions")))
                    {
                        string id = Jx.Str(s, "id");
                        if (id.Length == 0 || !seen.Add(id)) continue;
                        ChannelItem c = new ChannelItem();
                        c.Id = id;
                        c.Name = Fmt.Decode(Jx.Str(s, "name"));
                        c.Thumbnail = Jx.Str(s, "thumbnail");
                        res.Add(c);
                    }
            }
            catch { }
            return res;
        }

        // ------------------------------------------------------------ history (newest first)
        public static List<VideoItem> LoadHistory(int limit)
        {
            List<VideoItem> res = new List<VideoItem>();
            try
            {
                List<Dictionary<string, object>> rows = ReadNeDb("history.db");
                rows.Sort(delegate (Dictionary<string, object> a, Dictionary<string, object> b)
                {
                    return Jx.Long(b, "timeWatched").CompareTo(Jx.Long(a, "timeWatched"));
                });
                HashSet<string> seen = new HashSet<string>();
                foreach (Dictionary<string, object> r in rows)
                {
                    string id = Jx.Str(r, "videoId");
                    if (id.Length == 0 || !seen.Add(id)) continue;
                    VideoItem v = new VideoItem();
                    v.Id = id;
                    v.Title = Fmt.Decode(Jx.Str(r, "title"));
                    v.Author = Fmt.Decode(Jx.Str(r, "author"));
                    v.AuthorId = Jx.Str(r, "authorId");
                    v.Duration = (int)Jx.Long(r, "lengthSeconds");
                    v.DurationText = Fmt.Duration(v.Duration);
                    v.Thumbnail = Fmt.Thumb(id);
                    v.Published = Jx.Long(r, "published") / 1000;
                    v.Age = Fmt.Age(v.Published);
                    long vc = Jx.Long(r, "viewCount");
                    v.Views = vc > 0 ? Fmt.Views(vc) : "";
                    v.IsLive = Jx.Bool(r, "isLive");
                    v.WatchProgress = Jx.Dbl(r, "watchProgress");
                    v.WatchedAt = Jx.Long(r, "timeWatched") / 1000;
                    res.Add(v);
                    if (limit > 0 && res.Count >= limit) break;
                }
            }
            catch { }
            return res;
        }

        // ------------------------------------------------------------ subscription-cache.db feed (newest first)
        public static List<VideoItem> LoadSubscriptionCache()
        {
            List<VideoItem> res = new List<VideoItem>();
            try
            {
                HashSet<string> seen = new HashSet<string>();
                foreach (Dictionary<string, object> row in ReadNeDb("subscription-cache.db"))
                {
                    string chId = Jx.Str(row, "_id");
                    foreach (object o in Jx.L(Jx.Get(row, "videos")))
                    {
                        string id = Jx.Str(o, "videoId");
                        if (id.Length == 0 || !seen.Add(id)) continue;
                        if (Jx.Bool(o, "isUpcoming")) continue;
                        VideoItem v = new VideoItem();
                        v.Id = id;
                        v.Title = Fmt.Decode(Jx.Str(o, "title"));
                        v.Author = Fmt.Decode(Jx.Str(o, "author"));
                        v.AuthorId = Jx.Str(o, "authorId");
                        if (v.AuthorId.Length == 0) v.AuthorId = chId;
                        v.Duration = (int)Jx.Long(o, "lengthSeconds");
                        v.DurationText = Fmt.Duration(v.Duration);
                        v.Thumbnail = Fmt.Thumb(id);
                        v.Published = Jx.Long(o, "published") / 1000;
                        v.Age = Fmt.Age(v.Published);
                        long vc = Jx.Long(o, "viewCount");
                        v.Views = vc > 0 ? Fmt.Views(vc) : "";
                        v.IsLive = Jx.Bool(o, "liveNow");
                        // the cache carries no Shorts marker: a video of one minute or less counts as a Short
                        v.IsShort = !v.IsLive && v.Duration > 0 && v.Duration <= 60;
                        res.Add(v);
                    }
                }
                res.Sort(delegate (VideoItem a, VideoItem b) { return b.Published.CompareTo(a.Published); });
            }
            catch { }
            return res;
        }

        // ------------------------------------------------------------ playlists
        public static List<PlaylistItem> LoadPlaylists()
        {
            List<PlaylistItem> res = new List<PlaylistItem>();
            try
            {
                foreach (Dictionary<string, object> p in ReadNeDb("playlists.db"))
                {
                    PlaylistItem pl = new PlaylistItem();
                    pl.Id = Jx.Str(p, "_id");
                    pl.Title = Fmt.Decode(Jx.Str(p, "playlistName"));
                    foreach (object o in Jx.L(Jx.Get(p, "videos")))
                    {
                        string id = Jx.Str(o, "videoId");
                        if (id.Length == 0) continue;
                        VideoItem v = new VideoItem();
                        v.Id = id;
                        v.Title = Fmt.Decode(Jx.Str(o, "title"));
                        v.Author = Fmt.Decode(Jx.Str(o, "author"));
                        v.AuthorId = Jx.Str(o, "authorId");
                        v.Duration = (int)Jx.Long(o, "lengthSeconds");
                        v.DurationText = Fmt.Duration(v.Duration);
                        v.Thumbnail = Fmt.Thumb(id);
                        pl.Videos.Add(v);
                    }
                    pl.VideoCount = pl.Videos.Count;
                    if (pl.Videos.Count > 0) pl.Thumbnail = pl.Videos[0].Thumbnail;
                    res.Add(pl);
                }
            }
            catch { }
            return res;
        }

        // ------------------------------------------------------------ search history (newest first)
        public static List<string> LoadSearchHistory()
        {
            List<string> res = new List<string>();
            try
            {
                List<Dictionary<string, object>> rows = ReadNeDb("search-history.db");
                rows.Sort(delegate (Dictionary<string, object> a, Dictionary<string, object> b)
                {
                    return Jx.Long(b, "lastUpdatedAt").CompareTo(Jx.Long(a, "lastUpdatedAt"));
                });
                foreach (Dictionary<string, object> r in rows)
                {
                    string q = Jx.Str(r, "_id");
                    if (q.Length > 0) res.Add(q);
                }
            }
            catch { }
            return res;
        }

        // ------------------------------------------------------------ settings
        public static FreeTubeSettings LoadSettings()
        {
            FreeTubeSettings s = new FreeTubeSettings();
            s.DefaultQuality = 0;
            s.UseSponsorBlock = false;
            try
            {
                foreach (Dictionary<string, object> r in ReadNeDb("settings.db"))
                {
                    string id = Jx.Str(r, "_id");
                    if (id == "defaultQuality")
                    {
                        string q = Jx.Str(r, "value").ToLowerInvariant();
                        int n;
                        if (q == "auto") s.DefaultQuality = 0;
                        else if (int.TryParse(q.Replace("p", ""), out n)) s.DefaultQuality = n;
                    }
                    else if (id == "useSponsorBlock") s.UseSponsorBlock = Jx.Bool(r, "value");
                }
            }
            catch { }
            return s;
        }
    }

    // ====================================================================== Nomad's own store
    public static class NomadStore
    {
        static readonly object lk = new object();
        static string dir;

        public static string Dir
        {
            get
            {
                if (dir != null) return dir;
                string d = null;
                try { d = SeleneBrowserPlugin.DataDir; } catch { }
                if (string.IsNullOrEmpty(d))
                    d = Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lanternfall"), "Browser");
                try { Directory.CreateDirectory(d); } catch { }
                dir = d;
                return d;
            }
        }

        static JavaScriptSerializer Ser()
        {
            JavaScriptSerializer s = new JavaScriptSerializer();
            s.MaxJsonLength = int.MaxValue;
            s.RecursionLimit = 200;
            return s;
        }

        public static T Load<T>(string file, T fallback)
        {
            try
            {
                string p = Path.Combine(Dir, file);
                if (!File.Exists(p)) return fallback;
                string t = File.ReadAllText(p, Encoding.UTF8);
                if (t.Trim().Length == 0) return fallback;
                T v = Ser().Deserialize<T>(t);
                return v == null ? fallback : v;
            }
            catch { return fallback; }
        }

        public static void Save(string file, object value)
        {
            try
            {
                string p = Path.Combine(Dir, file);
                string tmp = p + ".tmp";
                File.WriteAllText(tmp, Ser().Serialize(value), new UTF8Encoding(false));
                if (File.Exists(p)) File.Delete(p);
                File.Move(tmp, p);
            }
            catch { }
        }

        // ---- prefs
        static Dictionary<string, string> prefs;

        public static string GetPref(string key, string def)
        {
            lock (lk)
            {
                if (prefs == null) prefs = Load<Dictionary<string, string>>("yt-prefs.json", new Dictionary<string, string>());
                string v;
                return prefs.TryGetValue(key, out v) ? v : def;
            }
        }

        public static void SetPref(string key, string value)
        {
            lock (lk)
            {
                if (prefs == null) prefs = Load<Dictionary<string, string>>("yt-prefs.json", new Dictionary<string, string>());
                prefs[key] = value;
                Save("yt-prefs.json", prefs);
            }
        }

        // ---- positions {id: [t, d]}
        static Dictionary<string, double[]> positions;

        static void EnsurePositions()
        {
            if (positions == null) positions = Load<Dictionary<string, double[]>>("yt-positions.json", new Dictionary<string, double[]>());
        }

        public static void SavePosition(string id, double t, double d)
        {
            if (string.IsNullOrEmpty(id)) return;
            lock (lk)
            {
                EnsurePositions();
                positions[id] = new double[] { Math.Round(t, 1), Math.Round(d, 1) };
                if (positions.Count > 3000)
                {
                    List<string> keys = new List<string>(positions.Keys);
                    for (int i = 0; i < 500; i++) positions.Remove(keys[i]);
                }
                Save("yt-positions.json", positions);
            }
        }

        /// <summary>Resume position in seconds: Nomad first, then FreeTube's watchProgress. 0 if finished/unknown.</summary>
        public static double GetPosition(string id)
        {
            double t = 0, d = 0;
            bool found = false;
            lock (lk)
            {
                EnsurePositions();
                double[] p;
                if (positions.TryGetValue(id, out p) && p != null && p.Length >= 2) { t = p[0]; d = p[1]; found = true; }
            }
            if (!found)
            {
                foreach (VideoItem v in FtHistoryCached())
                    if (v.Id == id) { t = v.WatchProgress; d = v.Duration; break; }
            }
            if (t < 5) return 0;
            if (d > 0 && t > d - 10) return 0;
            return t;
        }

        static List<VideoItem> ftHist; static DateTime ftHistAt;
        static List<VideoItem> FtHistoryCached()
        {
            lock (lk)
            {
                if (ftHist == null || (DateTime.UtcNow - ftHistAt).TotalSeconds > 60) { ftHist = FreeTubeData.LoadHistory(0); ftHistAt = DateTime.UtcNow; }
                return ftHist;
            }
        }

        // ---- history (Nomad's own, newest first)
        static List<VideoItem> history;

        public static void AddHistory(VideoItem v, double progress)
        {
            if (v == null || string.IsNullOrEmpty(v.Id)) return;
            lock (lk)
            {
                if (history == null) history = Load<List<VideoItem>>("yt-history.json", new List<VideoItem>());
                history.RemoveAll(delegate (VideoItem x) { return x.Id == v.Id; });
                VideoItem c = new VideoItem();
                c.Id = v.Id; c.Title = v.Title; c.Author = v.Author; c.AuthorId = v.AuthorId; c.Duration = v.Duration;
                c.DurationText = v.DurationText; c.Thumbnail = v.Thumbnail; c.Published = v.Published; c.Age = v.Age; c.Views = v.Views;
                c.IsLive = v.IsLive; c.WatchProgress = progress; c.WatchedAt = Fmt.NowUnix();
                history.Insert(0, c);
                if (history.Count > 500) history.RemoveRange(500, history.Count - 500);
                Save("yt-history.json", history);
            }
        }

        // Nomad's own watch history only (FreeTube's files are never written)
        public static void ClearHistory()
        {
            lock (lk)
            {
                history = new List<VideoItem>();
                Save("yt-history.json", history);
            }
        }

        public static List<VideoItem> NomadHistory()
        {
            lock (lk)
            {
                if (history == null) history = Load<List<VideoItem>>("yt-history.json", new List<VideoItem>());
                return new List<VideoItem>(history);
            }
        }

        /// <summary>Nomad history merged with FreeTube history, newest first, de-duplicated.</summary>
        public static List<VideoItem> MergedHistory(int limit)
        {
            List<VideoItem> all = NomadHistory();
            all.AddRange(FreeTubeData.LoadHistory(0));
            all.Sort(delegate (VideoItem a, VideoItem b) { return b.WatchedAt.CompareTo(a.WatchedAt); });
            List<VideoItem> res = new List<VideoItem>();
            HashSet<string> seen = new HashSet<string>();
            foreach (VideoItem v in all)
            {
                if (!seen.Add(v.Id)) continue;
                double p = GetPosition(v.Id);
                if (p > 0) v.WatchProgress = p;
                res.Add(v);
                if (limit > 0 && res.Count >= limit) break;
            }
            return res;
        }

        // ---- own search history
        static List<string> searches;

        public static void AddSearch(string q)
        {
            if (string.IsNullOrEmpty(q)) return;
            q = q.Trim();
            if (q.Length == 0) return;
            lock (lk)
            {
                if (searches == null) searches = Load<List<string>>("yt-searches.json", new List<string>());
                searches.RemoveAll(delegate (string s) { return string.Equals(s, q, StringComparison.OrdinalIgnoreCase); });
                searches.Insert(0, q);
                if (searches.Count > 60) searches.RemoveRange(60, searches.Count - 60);
                Save("yt-searches.json", searches);
            }
        }

        /// <summary>Own searches first, then FreeTube's.</summary>
        public static List<string> Searches(int limit)
        {
            List<string> res = new List<string>();
            lock (lk)
            {
                if (searches == null) searches = Load<List<string>>("yt-searches.json", new List<string>());
                res.AddRange(searches);
            }
            foreach (string s in FreeTubeData.LoadSearchHistory())
            {
                bool dup = false;
                foreach (string e in res) if (string.Equals(e, s, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                if (!dup) res.Add(s);
            }
            if (limit > 0 && res.Count > limit) res.RemoveRange(limit, res.Count - limit);
            return res;
        }

        // ---- Nomad's own subscriptions (in addition to the imported ones)
        static List<ChannelItem> subs;

        public static List<ChannelItem> OwnSubscriptions()
        {
            lock (lk)
            {
                if (subs == null) subs = Load<List<ChannelItem>>("yt-subs.json", new List<ChannelItem>());
                return new List<ChannelItem>(subs);
            }
        }

        public static void SetSubscribed(ChannelItem c, bool on)
        {
            if (c == null || string.IsNullOrEmpty(c.Id)) return;
            lock (lk)
            {
                if (subs == null) subs = Load<List<ChannelItem>>("yt-subs.json", new List<ChannelItem>());
                subs.RemoveAll(delegate (ChannelItem x) { return x.Id == c.Id; });
                if (on) subs.Add(c);
                Save("yt-subs.json", subs);
            }
        }

        // ---- subscription feed cache
        public const int FeedVersion = 2;   // 2 = items carry IsShort; older caches are ignored so the feed is refetched

        public class FeedCache
        {
            public int Version { get; set; }
            public long LastRefresh { get; set; }
            public List<VideoItem> Videos { get; set; }
            public FeedCache() { Videos = new List<VideoItem>(); }
        }

        public static FeedCache LoadFeed()
        {
            FeedCache f = Load<FeedCache>("yt-feed.json", new FeedCache());
            if (f.Version != FeedVersion) return new FeedCache();
            return f;
        }

        public static void SaveFeed(FeedCache f) { f.Version = FeedVersion; Save("yt-feed.json", f); }

        /// <summary>Ids that have any saved playback position (finished videos included).</summary>
        public static List<string> PositionIds()
        {
            lock (lk)
            {
                EnsurePositions();
                return new List<string>(positions.Keys);
            }
        }
    }
}
