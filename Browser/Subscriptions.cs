// Subscriptions feed: FreeTube subscription-cache.db + RSS refresh (3 parallel, backoff on 429). C# 5 only.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace SeleneBrowser
{
    public class FeedRefreshResult
    {
        public List<VideoItem> Videos { get; set; }
        public int Total { get; set; }
        public int Done { get; set; }
        public int Failed { get; set; }
        public bool RateLimited { get; set; }   // stopped early because of repeated HTTP 429
        public string Message { get; set; }     // human readable status, "" when all fine
        public FeedRefreshResult() { Videos = new List<VideoItem>(); Message = ""; }
    }

    public static class Subscriptions
    {
        const int MaxParallel = 3;
        const int MaxFeed = 600;
        static readonly object lk = new object();
        static int running;

        /// <summary>FreeTube channels (allChannels profile) + Nomad's own, de-duplicated, minus local unsubscribes.</summary>
        public static List<ChannelItem> Channels()
        {
            HashSet<string> removed = new HashSet<string>(NomadStore.RemovedIds());
            List<ChannelItem> res = FreeTubeData.LoadSubscriptions();
            res.RemoveAll(delegate (ChannelItem c) { return removed.Contains(c.Id); });
            HashSet<string> seen = new HashSet<string>();
            foreach (ChannelItem c in res) seen.Add(c.Id);
            foreach (ChannelItem c in NomadStore.OwnSubscriptions())
            {
                if (removed.Contains(c.Id)) continue;
                if (seen.Add(c.Id)) res.Add(c);
            }
            res.Sort(delegate (ChannelItem a, ChannelItem b) { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
            return res;
        }

        /// <summary>Instant feed: Nomad cache merged with FreeTube's subscription-cache.db, newest first.</summary>
        public static List<VideoItem> LoadCached()
        {
            List<VideoItem> nomad = NomadStore.LoadFeed().Videos;
            List<VideoItem> ft = FreeTubeData.LoadSubscriptionCache();
            return Merge(nomad, ft, ChannelIdSet());
        }

        static HashSet<string> ChannelIdSet()
        {
            HashSet<string> ids = new HashSet<string>();
            foreach (ChannelItem c in Channels()) ids.Add(c.Id);
            return ids;
        }

        static List<VideoItem> Merge(List<VideoItem> primary, List<VideoItem> secondary, HashSet<string> channelFilter)
        {
            Dictionary<string, VideoItem> map = new Dictionary<string, VideoItem>();
            foreach (VideoItem v in secondary) map[v.Id] = v;
            foreach (VideoItem v in primary)
            {
                VideoItem old;
                if (map.TryGetValue(v.Id, out old))
                {
                    if (v.Duration == 0 && old.Duration > 0) { v.Duration = old.Duration; v.DurationText = old.DurationText; }
                    if (v.Views.Length == 0) v.Views = old.Views;
                    if (old.IsLive && v.Duration == 0) v.IsLive = true;   // RSS never knows about live streams
                    // v.IsShort is kept as is: the primary (RSS-derived) value wins over the duration guess of FreeTube's cache
                }
                map[v.Id] = v;
            }
            List<VideoItem> list = new List<VideoItem>();
            foreach (VideoItem v in map.Values)
            {
                if (channelFilter != null && channelFilter.Count > 0 && v.AuthorId.Length > 0 && !channelFilter.Contains(v.AuthorId)) continue;
                list.Add(v);
            }
            list.Sort(delegate (VideoItem a, VideoItem b) { return b.Published.CompareTo(a.Published); });
            if (list.Count > MaxFeed) list.RemoveRange(MaxFeed, list.Count - MaxFeed);
            foreach (VideoItem v in list) v.Age = Fmt.Age(v.Published);
            return list;
        }

        public static bool IsRefreshing { get { return running != 0; } }

        /// <summary>
        /// Profile-switch hook. Subscriptions itself caches no personal data: Channels(), LoadCached()
        /// and Refresh() all re-read through NomadStore/FreeTubeData on every call. It delegates to the
        /// store so callers have one entry point; NomadStore deliberately does NOT call back into here
        /// (that would be a circular dependency).
        /// </summary>
        public static void Invalidate()
        {
            NomadStore.ReloadForProfile();
        }

        /// <summary>
        /// Refresh from channel RSS. progress(videos, done, total) is invoked (from a worker thread) with a merged,
        /// newest-first snapshot after each channel; marshal to the UI thread yourself. Never throws.
        /// </summary>
        public static Task<FeedRefreshResult> Refresh(Action<List<VideoItem>, int, int> progress)
        {
            return Task.Run(() => RefreshSync(progress));
        }

        static FeedRefreshResult RefreshSync(Action<List<VideoItem>, int, int> progress)
        {
            FeedRefreshResult result = new FeedRefreshResult();
            if (Interlocked.Exchange(ref running, 1) != 0)
            {
                result.Message = "A refresh is already running.";
                result.Videos = LoadCached();
                return result;
            }
            string profileAtStart = ProfileScope.CurrentId;
            try
            {
                List<ChannelItem> channels = Channels();
                HashSet<string> ids = new HashSet<string>();
                foreach (ChannelItem c in channels) ids.Add(c.Id);
                result.Total = channels.Count;

                Dictionary<string, VideoItem> fresh = new Dictionary<string, VideoItem>();
                List<VideoItem> baseCache = FreeTubeData.LoadSubscriptionCache();
                List<VideoItem> nomadCache = NomadStore.LoadFeed().Videos;
                List<VideoItem> basePrimary = new List<VideoItem>(nomadCache);

                int next = -1;
                int done = 0, failed = 0, total429 = 0;
                long pauseUntilTicks = 0;
                bool stop = false;
                object slk = new object();
                Random rnd = new Random();

                ThreadStart worker = delegate ()
                {
                    while (true)
                    {
                        if (stop) return;
                        int idx = Interlocked.Increment(ref next);
                        if (idx >= channels.Count) return;
                        ChannelItem ch = channels[idx];
                        List<VideoItem> got = null;
                        bool ok = false;
                        for (int attempt = 0; attempt < 4 && !stop; attempt++)
                        {
                            long pu = Interlocked.Read(ref pauseUntilTicks);
                            long wait = pu - DateTime.UtcNow.Ticks;
                            if (wait > 0) Thread.Sleep((int)Math.Min(30000, wait / TimeSpan.TicksPerMillisecond));
                            try
                            {
                                got = FetchRss(ch);
                                ok = true;
                                break;
                            }
                            catch (HttpFail hf)
                            {
                                if (hf.Status == 429)
                                {
                                    int n = Interlocked.Increment(ref total429);
                                    if (n >= 6) { stop = true; break; }
                                    int delay = (int)(2000 * Math.Pow(2, attempt)) + rnd.Next(0, 800);
                                    Interlocked.Exchange(ref pauseUntilTicks, DateTime.UtcNow.AddMilliseconds(delay).Ticks);
                                    continue;
                                }
                                break; // 404 etc: skip channel
                            }
                            catch { Thread.Sleep(500); }
                        }
                        List<VideoItem> snapshot = null;
                        int d, t = channels.Count;
                        lock (slk)
                        {
                            if (ok && got != null) foreach (VideoItem v in got) fresh[v.Id] = v;
                            else if (!stop) failed++;
                            d = ++done;
                            if (progress != null)
                                snapshot = Merge(new List<VideoItem>(fresh.Values), MergeBase(basePrimary, baseCache), ids);
                        }
                        if (progress != null && snapshot != null)
                        {
                            try { progress(snapshot, d, t); } catch { }
                        }
                        Thread.Sleep(120);
                    }
                };

                List<Thread> threads = new List<Thread>();
                for (int i = 0; i < MaxParallel; i++)
                {
                    Thread th = new Thread(worker);
                    th.IsBackground = true;
                    th.Start();
                    threads.Add(th);
                }
                foreach (Thread th in threads) th.Join();

                if (ProfileScope.CurrentId != profileAtStart)
                {
                    // Another profile signed in while the RSS workers were running: never write its feed
                    // with the previous profile's channels.
                    result.Message = "Profile changed while updating; showing this profile's cached feed.";
                    result.Videos = LoadCached();
                    return result;
                }
                List<VideoItem> merged;
                // Re-read the persisted cache at save time: a single-channel refresh (or another writer)
                // may have saved newer videos while the RSS workers were running.
                lock (lk) merged = MergeAndSave(new List<VideoItem>(fresh.Values), NomadStore.LoadFeed().Videos, baseCache, ids);
                result.Videos = merged;
                result.Done = done;
                result.Failed = failed;
                result.RateLimited = stop;
                if (stop) result.Message = "YouTube is rate limiting feed updates (HTTP 429). Showing cached videos; try again in a few minutes.";
                else if (failed > 0 && failed == channels.Count) result.Message = "Could not refresh subscriptions. Check your internet connection.";
                else if (failed > 0) result.Message = failed + " channel(s) could not be refreshed.";
            }
            catch (Exception e)
            {
                result.Message = Http.Friendly(e);
                try { result.Videos = LoadCached(); } catch { }
            }
            finally { Interlocked.Exchange(ref running, 0); }
            return result;
        }

        /// <summary>
        /// Refresh a single channel's RSS into the saved feed cache. Call from a Task (network I/O is synchronous).
        /// Returns the number of videos fetched and merged, or -1 on failure.
        /// </summary>
        public static Task<int> RefreshChannel(string channelId)
        {
            return Task.Run(() => RefreshChannelSync(channelId));
        }

        static int RefreshChannelSync(string channelId)
        {
            if (string.IsNullOrEmpty(channelId)) return -1;
            // Let an in-flight bulk refresh finish first: it captured its channel list and final cache
            // snapshot before this channel was subscribed, so saving now would be overwritten by it.
            int waited = 0;
            while (IsRefreshing && waited < 30000) { Thread.Sleep(200); waited += 200; }
            if (IsRefreshing) return -1;
            string profileAtStart = ProfileScope.CurrentId;
            try
            {
                List<ChannelItem> channels = Channels();
                ChannelItem ch = null;
                foreach (ChannelItem c in channels) if (c.Id == channelId) { ch = c; break; }
                if (ch == null)
                {
                    ch = new ChannelItem();
                    ch.Id = channelId;
                }
                List<VideoItem> got = FetchRss(ch);   // same RSS parsing as the bulk refresh
                HashSet<string> ids = new HashSet<string>();
                foreach (ChannelItem c in channels) ids.Add(c.Id);
                ids.Add(channelId);
                if (ProfileScope.CurrentId != profileAtStart) return -1;   // profile changed mid-fetch
                lock (lk) MergeAndSave(got, NomadStore.LoadFeed().Videos, FreeTubeData.LoadSubscriptionCache(), ids);
                return got.Count;
            }
            catch { return -1; }
        }

        // older cached videos (Nomad cache wins over FreeTube's cache)
        static List<VideoItem> MergeBase(List<VideoItem> nomad, List<VideoItem> ft)
        {
            Dictionary<string, VideoItem> m = new Dictionary<string, VideoItem>();
            foreach (VideoItem v in ft) m[v.Id] = v;
            foreach (VideoItem v in nomad) m[v.Id] = v;
            return new List<VideoItem>(m.Values);
        }

        // Shared by bulk and single-channel refresh: merge fresh RSS into both caches and persist yt-feed.json.
        // Callers hold Subscriptions.lk so two refreshes cannot overwrite each other's snapshot.
        static List<VideoItem> MergeAndSave(List<VideoItem> fresh, List<VideoItem> nomad, List<VideoItem> ft, HashSet<string> ids)
        {
            List<VideoItem> merged = Merge(new List<VideoItem>(fresh), MergeBase(nomad, ft), ids);
            if (fresh.Count > 0)
            {
                NomadStore.FeedCache fc = new NomadStore.FeedCache();
                fc.LastRefresh = Fmt.NowUnix();
                fc.Videos = merged;
                NomadStore.SaveFeed(fc);
            }
            return merged;
        }

        static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
        static readonly XNamespace Yt = "http://www.youtube.com/xml/schemas/2015";
        static readonly XNamespace Media = "http://search.yahoo.com/mrss/";

        static List<VideoItem> FetchRss(ChannelItem ch)
        {
            string xml = Http.Get("https://www.youtube.com/feeds/videos.xml?channel_id=" + ch.Id, 12000);
            XDocument doc = XDocument.Parse(xml);
            List<VideoItem> res = new List<VideoItem>();
            foreach (XElement e in doc.Root.Elements(Atom + "entry"))
            {
                string id = (string)e.Element(Yt + "videoId");
                if (string.IsNullOrEmpty(id)) continue;
                VideoItem v = new VideoItem();
                v.Id = id;
                v.Title = (string)e.Element(Atom + "title") ?? "";
                XElement a = e.Element(Atom + "author");
                v.Author = a != null ? ((string)a.Element(Atom + "name") ?? ch.Name) : ch.Name;
                v.AuthorId = ch.Id;
                v.Thumbnail = Fmt.Thumb(id);
                foreach (XElement ln in e.Elements(Atom + "link"))
                {
                    string rel = (string)ln.Attribute("rel");
                    string href = (string)ln.Attribute("href");
                    if ((rel == null || rel == "alternate") && href != null && href.IndexOf("/shorts/", StringComparison.OrdinalIgnoreCase) >= 0)
                        v.IsShort = true;
                }
                DateTimeOffset dto;
                string pub = (string)e.Element(Atom + "published");
                if (pub != null && DateTimeOffset.TryParse(pub, CultureInfo.InvariantCulture, DateTimeStyles.None, out dto))
                    v.Published = (long)(dto.UtcDateTime - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                v.Age = Fmt.Age(v.Published);
                XElement grp = e.Element(Media + "group");
                if (grp != null)
                {
                    XElement com = grp.Element(Media + "community");
                    XElement st = com != null ? com.Element(Media + "statistics") : null;
                    if (st != null)
                    {
                        long views;
                        if (long.TryParse((string)st.Attribute("views"), out views) && views > 0) v.Views = Fmt.Views(views);
                    }
                }
                res.Add(v);
            }
            return res;
        }
    }
}
