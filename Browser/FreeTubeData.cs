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

        // ------------------------------------------------ profile-scoped personal data (C# 5)
        // These are the only files NomadStore writes. FreeTube's own files stay global read-only input.
        static readonly string[] PersonalFiles = new string[] {
            "yt-subs.json", "yt-subs-removed.json", "yt-feed.json", "yt-history.json",
            "yt-positions.json", "yt-prefs.json", "yt-searches.json", "yt-card-seen.txt"
        };

        static bool IsPersonal(string file)
        {
            if (string.IsNullOrEmpty(file)) return false;
            for (int i = 0; i < PersonalFiles.Length; i++)
                if (string.Equals(PersonalFiles[i], file, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // The folder we attempted migration for this session. Keyed by path, so switching profiles
        // re-checks (cheaply) and never copies from another profile's folder.
        static string migratedFor;

        /// <summary>
        /// One-time move of legacy root-level personal files into a new profile folder. Reads only from
        /// the root (never from another profile), never overwrites, never deletes the originals.
        /// The destination yt-subs.json is the "already migrated" marker. No recursion: this uses raw
        /// root paths, not ScopedPath.
        /// </summary>
        static void EnsureMigrated(string profileDir)
        {
            lock (lk)
            {
            if (string.Equals(migratedFor, profileDir, StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                // One global one-shot: the legacy data belongs to the first profile that opens YouTube.
                // A marker at the root stops a second profile from receiving the same private data.
                // the folder first: with the marker already written (a first profile took the legacy data) a later profile
                // returned before creating it, and every Save() for that profile failed silently
                Directory.CreateDirectory(profileDir);
                string marker = Path.Combine(Dir, "yt-migrated.txt");
                if (File.Exists(marker)) { migratedFor = profileDir; return; }
                for (int i = 0; i < PersonalFiles.Length; i++)
                {
                    string src = Path.Combine(Dir, PersonalFiles[i]);
                    if (!File.Exists(src)) continue;
                    string dst = Path.Combine(profileDir, PersonalFiles[i]);
                    if (File.Exists(dst)) continue;
                    try { File.Copy(src, dst, false); } catch { }
                }
                try { File.WriteAllText(marker, Fmt.NowUnix().ToString()); } catch { }
            }
            catch { }
            migratedFor = profileDir;
            }
        }

        /// <summary>
        /// Resolves a personal file to &lt;DataDir&gt;\profiles\&lt;profileId&gt;\&lt;file&gt; when a Selene
        /// profile is signed in, otherwise to the legacy root path. Non-personal files stay at the root.
        /// </summary>
        static string ScopedPath(string file)
        {
            string root = Path.Combine(Dir, file);
            if (!IsPersonal(file)) return root;
            string pid = ProfileScope.CurrentId;
            if (string.IsNullOrEmpty(pid)) return root;
            string profileDir;
            try { profileDir = Path.Combine(Dir, "profiles", pid); }
            catch { return root; }
            EnsureMigrated(profileDir);
            return Path.Combine(profileDir, file);
        }

        /// <summary>Path of a personal file for the currently signed-in profile (for non-Nomad readers).</summary>
        public static string ScopedFile(string file) { return ScopedPath(file); }

        /// <summary>
        /// Drops every in-memory cache so the next read comes from the currently signed-in profile's
        /// folder. Call after ProfileScope.Refresh() reports a profile change.
        /// </summary>
        public static void ReloadForProfile()
        {
            // Safety net: pick up a profile switch even if the caller did not Refresh() first.
            try { ProfileScope.Refresh(); } catch { }
            lock (lk)
            {
                prefs = null;
                positions = null;
                ftHist = null;
                ftHistAt = DateTime.MinValue;
                history = null;
                searches = null;
                subs = null;
                removedSubs = null;
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
                string p = ScopedPath(file);
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
                string p = ScopedPath(file);
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
        // Local tombstones: channels imported from FreeTube that were unsubscribed here. FreeTube files stay read-only.
        static List<string> removedSubs;

        static void EnsureRemoved()
        {
            if (removedSubs == null) removedSubs = Load<List<string>>("yt-subs-removed.json", new List<string>());
        }

        public static List<ChannelItem> OwnSubscriptions()
        {
            lock (lk)
            {
                if (subs == null) subs = Load<List<ChannelItem>>("yt-subs.json", new List<ChannelItem>());
                return new List<ChannelItem>(subs);
            }
        }

        /// <summary>True when this channel was unsubscribed locally (hides a FreeTube import).</summary>
        public static bool IsRemoved(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            lock (lk)
            {
                EnsureRemoved();
                return removedSubs.Contains(id);
            }
        }

        /// <summary>Copy of the locally removed channel ids (never the live list).</summary>
        public static List<string> RemovedIds()
        {
            lock (lk)
            {
                EnsureRemoved();
                return new List<string>(removedSubs);
            }
        }

        public static void SetSubscribed(ChannelItem c, bool on)
        {
            if (c == null || string.IsNullOrEmpty(c.Id)) return;
            lock (lk)
            {
                if (subs == null) subs = Load<List<ChannelItem>>("yt-subs.json", new List<ChannelItem>());
                EnsureRemoved();
                subs.RemoveAll(delegate (ChannelItem x) { return x.Id == c.Id; });
                removedSubs.RemoveAll(delegate (string s) { return s == c.Id; });
                if (on) subs.Add(c);
                else if (!removedSubs.Contains(c.Id)) removedSubs.Add(c.Id);
                Save("yt-subs.json", subs);
                Save("yt-subs-removed.json", removedSubs);
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
