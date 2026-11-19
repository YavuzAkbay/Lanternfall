// Nomad YouTube backend: InnerTube (WEB for search/browse/next, ANDROID_VR for streams) with Invidious fallback.
// C# 5 only. Every public method returns a Task, runs off the UI thread and never throws.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SeleneBrowser
{
    internal class HttpFail : Exception
    {
        public int Status;
        public HttpFail(int status, string msg) : base(msg) { Status = status; }
    }

    internal static class Http
    {
        public const string WebUA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
        public const string VrUA = "com.google.android.apps.youtube.vr.oculus/1.65.10 (Linux; U; Android 12L; eureka-user Build/SQ3A.220605.009.A1) gzip";

        static Http()
        {
            try
            {
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | SecurityProtocolType.Tls;
                ServicePointManager.DefaultConnectionLimit = 64;
                ServicePointManager.Expect100Continue = false;
            }
            catch { }
        }

        public const string IosUA = "com.google.ios.youtube/20.10.4 (iPhone16,2; U; CPU iOS 18_3_2 like Mac OS X;)";

        public static void Init() { }

        public static HttpWebRequest Create(string method, string url, int timeoutMs, string userAgent)
        {
            HttpWebRequest r = (HttpWebRequest)WebRequest.Create(url);
            r.Method = method;
            r.Timeout = timeoutMs;
            r.ReadWriteTimeout = timeoutMs;
            r.UserAgent = userAgent ?? WebUA;
            r.Accept = "*/*";
            r.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            r.AllowAutoRedirect = true;
            r.MaximumAutomaticRedirections = 8;
            return r;
        }

        public static string Send(string method, string url, string body, IDictionary<string, string> headers, int timeoutMs)
        {
            string ua = null;
            if (headers != null && headers.ContainsKey("User-Agent")) ua = headers["User-Agent"];
            HttpWebRequest r = Create(method, url, timeoutMs, ua);
            if (headers != null)
            {
                foreach (KeyValuePair<string, string> kv in headers)
                {
                    if (kv.Key == "User-Agent") continue;
                    if (kv.Key == "Content-Type") r.ContentType = kv.Value;
                    else if (kv.Key == "Accept") r.Accept = kv.Value;
                    else if (kv.Key == "Referer") r.Referer = kv.Value;
                    else r.Headers[kv.Key] = kv.Value;
                }
            }
            if (body != null)
            {
                byte[] b = Encoding.UTF8.GetBytes(body);
                r.ContentLength = b.Length;
                using (Stream s = r.GetRequestStream()) s.Write(b, 0, b.Length);
            }
            try
            {
                using (HttpWebResponse resp = (HttpWebResponse)r.GetResponse())
                using (Stream rs = resp.GetResponseStream())
                using (StreamReader sr = new StreamReader(rs, Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch (WebException ex)
            {
                HttpWebResponse er = ex.Response as HttpWebResponse;
                if (er != null)
                {
                    int code = (int)er.StatusCode;
                    er.Close();
                    throw new HttpFail(code, "HTTP " + code);
                }
                throw;
            }
        }

        public static string Get(string url, int timeoutMs) { return Send("GET", url, null, null, timeoutMs); }

        public static string Friendly(Exception e)
        {
            HttpFail hf = e as HttpFail;
            if (hf != null)
            {
                if (hf.Status == 429) return "YouTube is limiting requests right now (HTTP 429). Please wait a moment and retry.";
                if (hf.Status == 403) return "YouTube refused the request (HTTP 403).";
                if (hf.Status == 404) return "Not found (HTTP 404).";
                if (hf.Status >= 500) return "The server had a problem (HTTP " + hf.Status + "). Try again later.";
                return "The server answered HTTP " + hf.Status + ".";
            }
            WebException we = e as WebException;
            if (we != null)
            {
                if (we.Status == WebExceptionStatus.Timeout) return "The connection timed out. Check your internet connection and retry.";
                if (we.Status == WebExceptionStatus.NameResolutionFailure || we.Status == WebExceptionStatus.ConnectFailure)
                    return "No internet connection (cannot reach YouTube).";
                return "Network problem: " + we.Message;
            }
            AggregateException ag = e as AggregateException;
            if (ag != null && ag.InnerException != null) return Friendly(ag.InnerException);
            return e == null ? "Unknown error." : "Something went wrong: " + e.Message;
        }
    }

    // ------------------------------------------------------------------ Invidious fallback
    internal static class Invidious
    {
        static readonly List<string> instances = new List<string>(new string[] { "https://yt.chocolatemoo53.com" });
        static bool refreshed;
        static readonly object lk = new object();
        public static string LastBase = "";

        static void Refresh()
        {
            lock (lk) { if (refreshed) return; refreshed = true; }
            try
            {
                string json = Http.Get("https://api.invidious.io/instances.json?sort_by=health", 8000);
                object root = Jx.Parse(json);
                List<string> found = new List<string>();
                foreach (object it in Jx.L(root))
                {
                    object info = Jx.Get(it, "1");
                    if (info == null) continue;
                    if (Jx.Str(info, "type") != "https") continue;
                    if (!Jx.Bool(info, "api")) continue;
                    string uri = Jx.Str(info, "uri").TrimEnd('/');
                    if (uri.Length > 0 && !found.Contains(uri)) found.Add(uri);
                    if (found.Count >= 8) break;
                }
                lock (lk)
                {
                    foreach (string f in found) if (!instances.Contains(f)) instances.Add(f);
                }
            }
            catch { }
        }

        public static string Get(string pathAndQuery) { return Get(pathAndQuery, null); }

        /// <summary>Like Get, but an instance whose answer fails accept(body) counts as failed and the next one is tried.</summary>
        public static string Get(string pathAndQuery, Func<string, bool> accept)
        {
            // instances answer in their own default language ("منذ ثانية" for "a second ago") unless asked for English
            if (pathAndQuery.IndexOf("hl=", StringComparison.Ordinal) < 0)
                pathAndQuery += (pathAndQuery.IndexOf('?') >= 0 ? "&" : "?") + "hl=en";
            Exception last = null;
            int attempts = 0;
            for (int round = 0; round < 2; round++)
            {
                List<string> snapshot;
                lock (lk) snapshot = new List<string>(instances);
                foreach (string inst in snapshot)
                {
                    if (attempts >= 5) break;
                    attempts++;
                    try
                    {
                        string res = Http.Get(inst + pathAndQuery, 9000);
                        if (res != null && res.Length > 1 && (accept == null || accept(res)))
                        {
                            LastBase = inst;
                            lock (lk) { instances.Remove(inst); instances.Insert(0, inst); }
                            return res;
                        }
                    }
                    catch (Exception e) { last = e; }
                }
                if (round == 0) Refresh();
            }
            throw last ?? new Exception("No Invidious instance reachable.");
        }

        public static VideoItem ToVideo(object v)
        {
            VideoItem it = new VideoItem();
            it.Id = Jx.Str(v, "videoId");
            if (it.Id.Length == 0) return null;
            it.Title = Jx.Str(v, "title");
            it.Author = Jx.Str(v, "author");
            it.AuthorId = Jx.Str(v, "authorId");
            it.Duration = (int)Jx.Long(v, "lengthSeconds");
            it.DurationText = Fmt.Duration(it.Duration);
            it.Thumbnail = Fmt.Thumb(it.Id);
            it.Published = Jx.Long(v, "published");
            it.Age = Jx.Str(v, "publishedText");
            long vc = Jx.Long(v, "viewCount");
            it.Views = vc > 0 ? Fmt.Views(vc) : Jx.Str(v, "viewCountText");
            it.IsLive = Jx.Bool(v, "liveNow");
            // a broken listing (seen on /trending) stamps every video "published now" with 0 views and no length:
            // that is no date at all - shown as unknown, never as NEW
            long now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            if (it.Published > now - 120 && vc <= 0 && it.Duration <= 0 && !it.IsLive)
            {
                it.Published = 0;
                it.Age = "";
                it.Views = "";
            }
            if (it.Age.Length == 0 && it.Published > 0) it.Age = Fmt.Age(it.Published);
            return it;
        }

        public static List<VideoItem> ToVideos(object arr)
        {
            List<VideoItem> res = new List<VideoItem>();
            foreach (object o in Jx.L(arr))
            {
                string type = Jx.Str(o, "type");
                if (type.Length > 0 && type != "video") continue;
                VideoItem v = ToVideo(o);
                if (v != null) res.Add(v);
            }
            return res;
        }
    }

    // ------------------------------------------------------------------ public API
    public static class YouTubeApi
    {
        const string WebVersion = "2.20251101.00.00";
        const string VrVersion = "1.65.10";
        const string WebKey = "AIzaSyAO_FJ2SlqU8Q4STEHLGCilw_Y9_11qcW8";
        const string VideosTabParams = "EgZ2aWRlb3PyBgQKAjoA";
        const string VideoFilter = "EgIQAQ==";

        static YouTubeApi() { Http.Init(); }

        // ---------------------------------------------------------- InnerTube plumbing
        static string WebContext()
        {
            return "{\"client\":{\"clientName\":\"WEB\",\"clientVersion\":\"" + WebVersion + "\",\"hl\":\"en\",\"gl\":\"US\"}}";
        }

        static object WebPost(string endpoint, string bodyFields)
        {
            string body = "{\"context\":" + WebContext() + (bodyFields.Length > 0 ? "," + bodyFields : "") + "}";
            Dictionary<string, string> h = new Dictionary<string, string>();
            h["Content-Type"] = "application/json";
            h["Origin"] = "https://www.youtube.com";
            h["X-YouTube-Client-Name"] = "1";
            h["X-YouTube-Client-Version"] = WebVersion;
            h["Cookie"] = "SOCS=CAI; CONSENT=YES+1";
            string url = "https://www.youtube.com/youtubei/v1/" + endpoint + "?prettyPrint=false&key=" + WebKey;
            string json = Http.Send("POST", url, body, h, 15000);
            return Jx.Parse(json);
        }

        static string Q(string s) { return Jx.ToJson(s ?? ""); }

        // ---------------------------------------------------------- renderer parsing
        static readonly string[] VideoKeys = new string[] { "videoRenderer", "gridVideoRenderer", "compactVideoRenderer", "playlistVideoRenderer", "lockupViewModel", "continuationItemRenderer" };

        static string BestThumb(string id) { return Fmt.Thumb(id); }

        static VideoItem ParseVideoRenderer(object v)
        {
            string id = Jx.Str(v, "videoId");
            if (id.Length == 0) return null;
            VideoItem it = new VideoItem();
            it.Id = id;
            it.Title = Jx.Text(Jx.Get(v, "title"));
            object a = Jx.Get(v, "ownerText") ?? Jx.Get(v, "longBylineText") ?? Jx.Get(v, "shortBylineText");
            it.Author = Jx.Text(a);
            it.AuthorId = Jx.FindString(a, "browseId", "UC") ?? "";
            string lt = Jx.Text(Jx.Get(v, "lengthText"));
            List<KeyValuePair<string, object>> ov = new List<KeyValuePair<string, object>>();
            Jx.Collect(Jx.Get(v, "thumbnailOverlays"), new string[] { "thumbnailOverlayTimeStatusRenderer" }, ov);
            foreach (KeyValuePair<string, object> kv in ov)
            {
                string ostyle = Jx.Str(kv.Value, "style");
                if (ostyle == "LIVE") it.IsLive = true;
                if (ostyle == "SHORTS") it.IsShort = true;
                if (lt.Length == 0) lt = Jx.Text(Jx.Get(kv.Value, "text"));
            }
            if (IsShortNav(Jx.Get(v, "navigationEndpoint"))) it.IsShort = true;
            List<KeyValuePair<string, object>> badges = new List<KeyValuePair<string, object>>();
            Jx.Collect(Jx.Get(v, "badges"), new string[] { "metadataBadgeRenderer" }, badges);
            foreach (KeyValuePair<string, object> kv in badges)
                if (Jx.Str(kv.Value, "style").Contains("LIVE_NOW") || Jx.Str(kv.Value, "label").ToUpperInvariant() == "LIVE") it.IsLive = true;
            it.Duration = Fmt.ParseDuration(lt);
            it.DurationText = it.Duration > 0 ? Fmt.Duration(it.Duration) : (lt.Trim().Length < 10 && !it.IsLive ? lt.Trim() : "");
            it.Views = Jx.Text(Jx.Get(v, "viewCountText"));
            if (it.Views.Length == 0) it.Views = Jx.Text(Jx.Get(v, "shortViewCountText"));
            if (it.Views.IndexOf("watching", StringComparison.OrdinalIgnoreCase) >= 0) it.IsLive = true;
            it.Age = Jx.Text(Jx.Get(v, "publishedTimeText"));
            it.Published = Fmt.ParseAge(it.Age);
            it.Thumbnail = BestThumb(id);
            return it;
        }

        // Shorts open through a reelWatchEndpoint / a /shorts/ URL instead of /watch
        static bool IsShortNav(object nav)
        {
            if (nav == null) return false;
            Dictionary<string, object> d = Jx.D(nav);
            if (d != null && d.ContainsKey("reelWatchEndpoint")) return true;
            string u = Jx.FindString(nav, "url", null);
            if (u != null && u.IndexOf("/shorts/", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (Jx.FindString(nav, "webPageType", "WEB_PAGE_TYPE_SHORTS") != null) return true;
            List<KeyValuePair<string, object>> hit = new List<KeyValuePair<string, object>>();
            Jx.Collect(nav, new string[] { "reelWatchEndpoint" }, hit);
            return hit.Count > 0;
        }

        static VideoItem ParseLockup(object v)
        {
            string type = Jx.Str(v, "contentType");
            if (type.Length > 0 && type.IndexOf("VIDEO", StringComparison.OrdinalIgnoreCase) < 0) return null;
            string id = Jx.Str(v, "contentId");
            if (id.Length != 11) return null;
            VideoItem it = new VideoItem();
            it.Id = id;
            object meta = Jx.Get(v, "metadata", "lockupMetadataViewModel");
            it.Title = Jx.Text(Jx.Get(meta, "title"));
            it.AuthorId = Jx.FindString(meta, "browseId", "UC") ?? "";
            List<KeyValuePair<string, object>> rows = new List<KeyValuePair<string, object>>();
            Jx.Collect(Jx.Get(meta, "metadata"), new string[] { "metadataParts" }, rows);
            foreach (KeyValuePair<string, object> kv in rows)
            {
                foreach (object part in Jx.L(kv.Value))
                {
                    string t = Jx.Text(Jx.Get(part, "text"));
                    if (t.Length == 0) continue;
                    string lo = t.ToLowerInvariant();
                    if (lo.IndexOf("watching") >= 0) { it.IsLive = true; it.Views = t; }
                    else if (lo.EndsWith("views") || lo.EndsWith("view")) it.Views = t;
                    else if (lo.EndsWith("ago") || lo.StartsWith("streamed") || lo.StartsWith("premiered")) it.Age = t;
                    else if (it.Author.Length == 0) it.Author = t;
                }
            }
            if (IsShortNav(Jx.Get(v, "rendererContext"))) it.IsShort = true;
            List<KeyValuePair<string, object>> bdg = new List<KeyValuePair<string, object>>();
            Jx.Collect(v, new string[] { "thumbnailBadgeViewModel", "badgeViewModel", "metadataBadgeRenderer" }, bdg);
            foreach (KeyValuePair<string, object> kv in bdg)
            {
                if (kv.Key == "metadataBadgeRenderer")
                {
                    if (Jx.Str(kv.Value, "style").Contains("LIVE_NOW") || Jx.Str(kv.Value, "label").ToUpperInvariant() == "LIVE") it.IsLive = true;
                    continue;
                }
                string t = Jx.Text(Jx.Get(kv.Value, "text")).Trim();
                if (t.Length == 0) t = Jx.Str(kv.Value, "badgeText").Trim();
                if (t.ToUpperInvariant() == "LIVE" || Jx.Str(kv.Value, "badgeStyle").Contains("LIVE")) it.IsLive = true;
                else if (it.Duration == 0)
                {
                    int d = Fmt.ParseDuration(t);
                    if (d > 0) { it.Duration = d; it.DurationText = Fmt.Duration(d); }
                }
            }
            it.Published = Fmt.ParseAge(it.Age);
            it.Thumbnail = BestThumb(id);
            return it;
        }

        /// <summary>Walk any InnerTube response and gather video items + the last continuation token.</summary>
        static void ParseVideos(object root, PageResult<VideoItem> res, string defAuthor, string defAuthorId)
        {
            object main = Jx.Get(root, "contents");
            if (main != null) root = main;
            List<KeyValuePair<string, object>> hits = new List<KeyValuePair<string, object>>();
            Jx.Collect(root, VideoKeys, hits);
            HashSet<string> seen = new HashSet<string>();
            string cont = "";
            foreach (KeyValuePair<string, object> kv in hits)
            {
                if (kv.Key == "continuationItemRenderer")
                {
                    string tok = Jx.Str(kv.Value, "continuationEndpoint", "continuationCommand", "token");
                    if (tok.Length == 0) tok = Jx.FindString(kv.Value, "token", null) ?? "";
                    if (tok.Length > 0) cont = tok;
                    continue;
                }
                VideoItem it = kv.Key == "lockupViewModel" ? ParseLockup(kv.Value) : ParseVideoRenderer(kv.Value);
                if (it == null || it.Id.Length == 0 || !seen.Add(it.Id)) continue;
                if (it.Author.Length == 0 && defAuthor != null) it.Author = defAuthor;
                if (it.AuthorId.Length == 0 && defAuthorId != null) it.AuthorId = defAuthorId;
                res.Items.Add(it);
            }
            res.Continuation = cont;
        }

        static void SafeInv(Action a) { try { a(); } catch { } }

        // ---------------------------------------------------------- Search
        public static Task<PageResult<VideoItem>> Search(string query, string continuation)
        {
            return Task.Run(() => SearchSync(query, continuation));
        }

        static PageResult<VideoItem> SearchSync(string query, string continuation)
        {
            string err = "";
            try
            {
                if (string.IsNullOrEmpty(continuation) || !continuation.StartsWith("inv|", StringComparison.Ordinal))
                {
                    string fields = string.IsNullOrEmpty(continuation)
                        ? "\"query\":" + Q(query) + ",\"params\":\"" + VideoFilter + "\""
                        : "\"continuation\":" + Q(continuation);
                    object root = WebPost("search", fields);
                    PageResult<VideoItem> r = new PageResult<VideoItem>();
                    r.Source = "innertube";
                    ParseVideos(root, r, null, null);
                    if (r.Items.Count > 0) return r;
                    err = "No results.";
                    if (!string.IsNullOrEmpty(continuation)) return r;
                }
            }
            catch (Exception e) { err = Http.Friendly(e); }
            // Invidious fallback
            try
            {
                int page = 1; string q = query;
                if (!string.IsNullOrEmpty(continuation) && continuation.StartsWith("inv|", StringComparison.Ordinal))
                {
                    string[] p = continuation.Split(new char[] { '|' }, 3);
                    page = int.Parse(p[1], CultureInfo.InvariantCulture); q = p[2];
                }
                string json = Invidious.Get("/api/v1/search?type=video&page=" + page + "&q=" + Uri.EscapeDataString(q ?? ""));
                PageResult<VideoItem> r = new PageResult<VideoItem>();
                r.Source = "invidious";
                r.Items = Invidious.ToVideos(Jx.Parse(json));
                if (r.Items.Count > 0) r.Continuation = "inv|" + (page + 1) + "|" + q;
                return r;
            }
            catch (Exception e2)
            {
                return PageResult<VideoItem>.Fail(err.Length > 0 && err != "No results." ? err : Http.Friendly(e2));
            }
        }

        // ---------------------------------------------------------- Suggestions
        public static Task<List<string>> SearchSuggestions(string query)
        {
            return Task.Run(() =>
            {
                List<string> res = new List<string>();
                try
                {
                    if (string.IsNullOrEmpty(query)) return res;
                    string t = Http.Get("https://suggestqueries-clients6.youtube.com/complete/search?client=youtube&ds=yt&hl=en&q=" + Uri.EscapeDataString(query), 6000);
                    int a = t.IndexOf('['), b = t.LastIndexOf(']');
                    if (a < 0 || b <= a) return res;
                    object root = Jx.Parse(t.Substring(a, b - a + 1));
                    foreach (object s in Jx.L(Jx.Get(root, "1")))
                    {
                        string txt = Jx.Str(s, "0");
                        if (txt.Length > 0) res.Add(txt);
                    }
                }
                catch { }
                return res;
            });
        }

        // ---------------------------------------------------------- Trending / Home
        public static Task<PageResult<VideoItem>> Trending(string continuation)
        {
            return Task.Run(() => TrendingSync(continuation));
        }

        static PageResult<VideoItem> TrendingSync(string continuation)
        {
            if (!string.IsNullOrEmpty(continuation))
            {
                if (continuation.StartsWith("inv|", StringComparison.Ordinal) || continuation.StartsWith("srch|", StringComparison.Ordinal))
                {
                    if (continuation.StartsWith("srch|", StringComparison.Ordinal)) return SearchSync("popular videos", continuation.Substring(5));
                    return PageResult<VideoItem>.Fail("");
                }
                try
                {
                    PageResult<VideoItem> r = new PageResult<VideoItem>();
                    r.Source = "innertube";
                    ParseVideos(WebPost("browse", "\"continuation\":" + Q(continuation)), r, null, null);
                    return r;
                }
                catch (Exception e) { return PageResult<VideoItem>.Fail(Http.Friendly(e)); }
            }
            try
            {
                PageResult<VideoItem> r = new PageResult<VideoItem>();
                r.Source = "innertube:FEtrending";
                ParseVideos(WebPost("browse", "\"browseId\":\"FEtrending\""), r, null, null);
                if (r.Items.Count > 0) return r;
            }
            catch { }
            try
            {
                string json = Invidious.Get("/api/v1/trending");
                PageResult<VideoItem> r = new PageResult<VideoItem>();
                r.Source = "invidious:trending";
                r.Items = Invidious.ToVideos(Jx.Parse(json));
                if (r.Items.Count > 0) return r;
            }
            catch { }
            PageResult<VideoItem> p = SearchSync("popular videos this week", null);
            if (p.Ok) { p.Source = "search:popular"; if (p.Continuation.Length > 0) p.Continuation = "srch|" + p.Continuation; }
            return p;
        }

        // ---------------------------------------------------------- Channel
        public static Task<PageResult<VideoItem>> Channel(string channelId, string continuation)
        {
            return Task.Run(() => ChannelSync(channelId, continuation));
        }

        static string FixUrl(string u)
        {
            if (string.IsNullOrEmpty(u)) return "";
            if (u.StartsWith("//", StringComparison.Ordinal)) return "https:" + u;
            return u;
        }

        static string LastThumbUrl(object arr)
        {
            return FixUrl(Jx.Str(Jx.Get(arr, "last"), "url"));
        }

        static ChannelItem ParseChannelHeader(object root, string channelId)
        {
            ChannelItem c = new ChannelItem();
            c.Id = channelId;
            object meta = Jx.Get(root, "metadata", "channelMetadataRenderer");
            c.Name = Jx.Str(meta, "title");
            c.Description = Jx.Str(meta, "description");
            c.Thumbnail = LastThumbUrl(Jx.Get(meta, "avatar", "thumbnails"));
            object hdr = Jx.Get(root, "header");
            if (c.Name.Length == 0 || c.Thumbnail.Length == 0)
            {
                List<KeyValuePair<string, object>> av = new List<KeyValuePair<string, object>>();
                Jx.Collect(hdr, new string[] { "avatarViewModel", "avatar" }, av);
                foreach (KeyValuePair<string, object> kv in av)
                {
                    string u = LastThumbUrl(Jx.Get(kv.Value, "image", "sources"));
                    if (u.Length == 0) u = LastThumbUrl(Jx.Get(kv.Value, "avatarViewModel", "image", "sources"));
                    if (u.Length == 0) u = LastThumbUrl(Jx.Get(kv.Value, "thumbnails"));
                    if (u.Length > 0) { if (c.Thumbnail.Length == 0) c.Thumbnail = u; break; }
                }
            }
            List<KeyValuePair<string, object>> bn = new List<KeyValuePair<string, object>>();
            Jx.Collect(hdr, new string[] { "imageBannerViewModel", "banner" }, bn);
            foreach (KeyValuePair<string, object> kv in bn)
            {
                string u = LastThumbUrl(Jx.Get(kv.Value, "image", "sources"));
                if (u.Length == 0) u = LastThumbUrl(Jx.Get(kv.Value, "imageBannerViewModel", "image", "sources"));
                if (u.Length == 0) u = LastThumbUrl(Jx.Get(kv.Value, "thumbnails"));
                if (u.Length > 0) { c.Banner = u; break; }
            }
            if (c.Name.Length == 0)
            {
                c.Name = Jx.Text(Jx.Get(hdr, "c4TabbedHeaderRenderer", "title"));
                if (c.Name.Length == 0) c.Name = Jx.Text(Jx.Get(hdr, "pageHeaderRenderer", "pageTitle"));
            }
            // subscribers: any text mentioning "subscribers" in the header
            List<KeyValuePair<string, object>> parts = new List<KeyValuePair<string, object>>();
            Jx.Collect(hdr, new string[] { "metadataParts" }, parts);
            foreach (KeyValuePair<string, object> kv in parts)
                foreach (object p in Jx.L(kv.Value))
                {
                    string t = Jx.Text(Jx.Get(p, "text"));
                    if (t.IndexOf("subscriber", StringComparison.OrdinalIgnoreCase) >= 0) { c.Subscribers = t; break; }
                }
            if (c.Subscribers.Length == 0)
                c.Subscribers = Jx.Text(Jx.Get(hdr, "c4TabbedHeaderRenderer", "subscriberCountText"));
            return c;
        }

        static PageResult<VideoItem> ChannelSync(string channelId, string continuation)
        {
            string err;
            try
            {
                if (!string.IsNullOrEmpty(continuation) && continuation.StartsWith("inv|", StringComparison.Ordinal))
                    return ChannelInvidious(channelId, continuation.Substring(4), false);
                string fields = string.IsNullOrEmpty(continuation)
                    ? "\"browseId\":" + Q(channelId) + ",\"params\":\"" + VideosTabParams + "\""
                    : "\"continuation\":" + Q(continuation);
                object root = WebPost("browse", fields);
                PageResult<VideoItem> r = new PageResult<VideoItem>();
                r.Source = "innertube";
                if (string.IsNullOrEmpty(continuation)) r.Header = ParseChannelHeader(root, channelId);
                string dn = r.Header != null ? r.Header.Name : null;
                ParseVideos(root, r, dn, channelId);
                if (r.Items.Count > 0 || !string.IsNullOrEmpty(continuation)) return r;
                err = "This channel has no videos.";
                if (r.Header != null && r.Header.Name.Length > 0) return r;
            }
            catch (Exception e) { err = Http.Friendly(e); }
            try { return ChannelInvidious(channelId, continuation, string.IsNullOrEmpty(continuation)); }
            catch (Exception e2) { return PageResult<VideoItem>.Fail(err.Length > 0 && err != "This channel has no videos." ? err : Http.Friendly(e2)); }
        }

        static PageResult<VideoItem> ChannelInvidious(string channelId, string cont, bool header)
        {
            PageResult<VideoItem> r = new PageResult<VideoItem>();
            r.Source = "invidious";
            string path = "/api/v1/channels/" + channelId + "/videos" + (string.IsNullOrEmpty(cont) ? "" : "?continuation=" + Uri.EscapeDataString(cont));
            object root = Jx.Parse(Invidious.Get(path));
            r.Items = Invidious.ToVideos(Jx.Get(root, "videos"));
            string next = Jx.Str(root, "continuation");
            if (next.Length > 0) r.Continuation = "inv|" + next;
            if (header)
            {
                try
                {
                    object ch = Jx.Parse(Invidious.Get("/api/v1/channels/" + channelId));
                    ChannelItem c = new ChannelItem();
                    c.Id = channelId; c.Name = Jx.Str(ch, "author"); c.Description = Jx.Str(ch, "description");
                    c.Thumbnail = FixUrl(Jx.Str(Jx.Get(ch, "authorThumbnails", "last"), "url"));
                    c.Banner = FixUrl(Jx.Str(Jx.Get(ch, "authorBanners", "0"), "url"));
                    long sc = Jx.Long(ch, "subCount");
                    if (sc > 0) c.Subscribers = Fmt.Views(sc).Replace(" views", " subscribers").Replace(" view", " subscriber");
                    r.Header = c;
                }
                catch { }
            }
            return r;
        }

        // ---------------------------------------------------------- Related
        public static Task<PageResult<VideoItem>> Related(string videoId)
        {
            return Task.Run(() => RelatedSync(videoId));
        }

        static PageResult<VideoItem> RelatedSync(string videoId)
        {
            string err = "";
            try
            {
                object root = WebPost("next", "\"videoId\":" + Q(videoId));
                object sec = Jx.Get(root, "contents", "twoColumnWatchNextResults", "secondaryResults", "secondaryResults", "results");
                PageResult<VideoItem> r = new PageResult<VideoItem>();
                r.Source = "innertube";
                ParseVideos(sec ?? root, r, null, null);
                r.Items.RemoveAll(delegate (VideoItem v) { return v.Id == videoId; });
                if (r.Items.Count > 0) return r;
                err = "No related videos.";
            }
            catch (Exception e) { err = Http.Friendly(e); }
            try
            {
                object root = Jx.Parse(Invidious.Get("/api/v1/videos/" + videoId + "?fields=recommendedVideos"));
                PageResult<VideoItem> r = new PageResult<VideoItem>();
                r.Source = "invidious";
                r.Items = Invidious.ToVideos(Jx.Get(root, "recommendedVideos"));
                if (r.Items.Count > 0) return r;
            }
            catch { }
            return PageResult<VideoItem>.Fail(err.Length > 0 ? err : "No related videos.");
        }

        // ---------------------------------------------------------- Streams
        public static Task<Result<StreamInfo>> GetStreams(string videoId)
        {
            return Task.Run(() => GetStreamsSync(videoId));
        }

        static readonly Regex LiveNowRx = new Regex("\"liveNow\"\\s*:\\s*true", RegexOptions.Compiled);
        static readonly Regex HlsUrlRx = new Regex("\"hlsUrl\"\\s*:\\s*\"[^\"]+", RegexOptions.Compiled);

        // an Invidious instance that reports a live stream without its HLS manifest is skipped
        static bool AcceptStreamBody(string body)
        {
            return !LiveNowRx.IsMatch(body) || HlsUrlRx.IsMatch(body);
        }

        const string IosVersion = "20.10.4";

        static object FetchIosPlayer(string videoId)
        {
            string body = "{\"context\":{\"client\":{\"clientName\":\"IOS\",\"clientVersion\":\"" + IosVersion + "\",\"deviceMake\":\"Apple\",\"deviceModel\":\"iPhone16,2\",\"osName\":\"iPhone\",\"osVersion\":\"18.3.2.22D82\",\"hl\":\"en\",\"gl\":\"US\"}},\"videoId\":" + Q(videoId) + ",\"contentCheckOk\":true,\"racyCheckOk\":true}";
            Dictionary<string, string> h = new Dictionary<string, string>();
            h["Content-Type"] = "application/json";
            h["User-Agent"] = Http.IosUA;
            h["X-YouTube-Client-Name"] = "5";
            h["X-YouTube-Client-Version"] = IosVersion;
            return Jx.Parse(Http.Send("POST", "https://www.youtube.com/youtubei/v1/player?prettyPrint=false", body, h, 15000));
        }

        static bool IsLiveVideoFormat(StreamFormat f)
        {
            if (f.Height <= 0 || f.Url.Length == 0) return false;
            string c = f.Codecs.ToLowerInvariant();
            if (f.MimeBase.Equals("video/webm", StringComparison.OrdinalIgnoreCase))
                return (c.StartsWith("vp9") || c.StartsWith("vp09")) && !c.StartsWith("vp09.02") && c != "vp9.2";   // no HDR (Profile 2, either spelling)
            if (f.MimeBase.Equals("video/mp4", StringComparison.OrdinalIgnoreCase)) return c.StartsWith("avc1");
            return false;
        }

        // IOS client live streams: plain-url adaptive formats, no HLS manifest. Null when this is not a usable live stream.
        static StreamInfo ParseIosLive(object root, string videoId)
        {
            if (Jx.Str(root, "playabilityStatus", "status") != "OK") return null;
            StreamInfo si = ParseInnerTubeStreams(root, videoId);
            if (!si.IsLive) return null;
            StreamFormat audio = null;
            foreach (StreamFormat f in si.Video) if (IsLiveVideoFormat(f)) si.LiveVideo.Add(f);
            foreach (StreamFormat f in si.Audio)
            {
                if (!f.MimeBase.Equals("audio/mp4", StringComparison.OrdinalIgnoreCase) || f.Url.Length == 0) continue;
                if (audio == null || f.Itag == 140 || (audio.Itag != 140 && f.Itag == 139)) audio = f;
            }
            if (si.LiveVideo.Count == 0 || audio == null) return null;
            si.LiveAudio = audio;
            long sec = Jx.Long(root, "streamingData", "adaptiveFormats", "0", "targetDurationSec");
            si.SegmentSeconds = sec >= 1 && sec <= 10 ? (int)sec : 5;
            si.Video.Clear(); si.Audio.Clear(); si.Muxed.Clear();
            si.HlsUrl = "";
            si.Source = "innertube-live";
            return si;
        }

        static StreamInfo TryIosLive(string videoId)
        {
            try { return ParseIosLive(FetchIosPlayer(videoId), videoId); }
            catch { return null; }
        }

        /// <summary>Fresh itag -> url map for a live video (format urls expire after a few hours). Null on failure.</summary>
        public static Dictionary<int, string> RefreshLiveUrls(string videoId)
        {
            StreamInfo si = TryIosLive(videoId);
            if (si == null) return null;
            Dictionary<int, string> m = new Dictionary<int, string>();
            foreach (StreamFormat f in si.LiveVideo) m[f.Itag] = f.Url;
            m[si.LiveAudio.Itag] = si.LiveAudio.Url;
            return m;
        }

        public static Result<StreamInfo> GetStreamsSync(string videoId)
        {
            string err = "";
            StreamInfo itLive = null;
            bool tryIos = false, iosTried = false;
            try
            {
                string body = "{\"context\":{\"client\":{\"clientName\":\"ANDROID_VR\",\"clientVersion\":\"" + VrVersion + "\",\"deviceMake\":\"Oculus\",\"deviceModel\":\"Quest 3\",\"androidSdkVersion\":32,\"osName\":\"Android\",\"osVersion\":\"12L\",\"hl\":\"en\",\"gl\":\"US\"}},\"videoId\":" + Q(videoId) + ",\"contentCheckOk\":true,\"racyCheckOk\":true}";
                Dictionary<string, string> h = new Dictionary<string, string>();
                h["Content-Type"] = "application/json";
                h["User-Agent"] = Http.VrUA;
                h["X-YouTube-Client-Name"] = "28";
                h["X-YouTube-Client-Version"] = VrVersion;
                string json = Http.Send("POST", "https://www.youtube.com/youtubei/v1/player?prettyPrint=false", body, h, 15000);
                object root = Jx.Parse(json);
                string status = Jx.Str(root, "playabilityStatus", "status");
                if (status != "OK")
                {
                    tryIos = true;   // live streams answer LOGIN_REQUIRED here
                    string reason = Jx.Str(root, "playabilityStatus", "reason");
                    err = reason.Length > 0 ? reason : "This video cannot be played (" + status + ").";
                }
                else
                {
                    StreamInfo si = ParseInnerTubeStreams(root, videoId);
                    if (si.IsLive) { itLive = si; tryIos = true; }   // live: IOS segments first, then the Invidious HLS below; this one is the last resort
                    else if (si.Video.Count > 0 && si.Audio.Count > 0 || si.HlsUrl.Length > 0) return Result<StreamInfo>.Success(si);
                    else err = "No playable streams were returned.";
                }
            }
            catch (Exception e) { err = Http.Friendly(e); }
            if (tryIos)
            {
                iosTried = true;
                StreamInfo ios = TryIosLive(videoId);
                if (ios != null) return Result<StreamInfo>.Success(ios);
            }
            try
            {
                // ANDROID_VR answers LOGIN_REQUIRED for live streams, so the Invidious lookup also covers them (liveNow + hlsUrl)
                object root = Jx.Parse(Invidious.Get("/api/v1/videos/" + videoId + "?local=true", AcceptStreamBody));
                StreamInfo si = ParseInvidiousStreams(root, videoId, Invidious.LastBase);
                if (si.IsLive && !iosTried)
                {
                    iosTried = true;
                    StreamInfo ios2 = TryIosLive(videoId);
                    if (ios2 != null) return Result<StreamInfo>.Success(ios2);
                }
                if (si.IsLive && si.HlsUrl.Length > 0) return Result<StreamInfo>.Success(si);
                if (!si.IsLive && si.Video.Count > 0 && si.Audio.Count > 0 && itLive == null) return Result<StreamInfo>.Success(si);
            }
            catch { }
            if (itLive != null && itLive.HlsUrl.Length > 0) return Result<StreamInfo>.Success(itLive);
            if (itLive != null) return Result<StreamInfo>.Fail("This live stream could not be loaded right now.");
            return Result<StreamInfo>.Fail(err.Length > 0 ? err : "No playable streams were returned.");
        }

        static readonly Regex MimeRx = new Regex("^\\s*([^;\\s]+)\\s*(?:;\\s*codecs=\"([^\"]*)\")?", RegexOptions.Compiled);

        static void SplitMime(string mime, StreamFormat f)
        {
            f.Mime = mime ?? "";
            Match m = MimeRx.Match(f.Mime);
            if (m.Success) { f.MimeBase = m.Groups[1].Value; f.Codecs = m.Groups[2].Value; }
            f.IsVideo = f.MimeBase.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
        }

        static StreamInfo ParseInnerTubeStreams(object root, string videoId)
        {
            StreamInfo si = new StreamInfo();
            si.VideoId = videoId;
            si.Source = "innertube";
            si.Title = Jx.Str(root, "videoDetails", "title");
            si.Author = Jx.Str(root, "videoDetails", "author");
            si.AuthorId = Jx.Str(root, "videoDetails", "channelId");
            si.Duration = (int)Jx.Long(root, "videoDetails", "lengthSeconds");
            si.IsLive = Jx.Bool(root, "videoDetails", "isLive") || Jx.Bool(root, "videoDetails", "isLiveContent") && Jx.Str(root, "streamingData", "hlsManifestUrl").Length > 0;
            si.HlsUrl = Jx.Str(root, "streamingData", "hlsManifestUrl");
            foreach (object o in Jx.L(Jx.Get(root, "streamingData", "adaptiveFormats")))
            {
                StreamFormat f = new StreamFormat();
                f.Url = Jx.Str(o, "url");
                if (f.Url.Length == 0) continue; // ciphered: unsupported
                f.Itag = (int)Jx.Long(o, "itag");
                SplitMime(Jx.Str(o, "mimeType"), f);
                f.Width = (int)Jx.Long(o, "width");
                f.Height = (int)Jx.Long(o, "height");
                f.Fps = (int)Jx.Long(o, "fps");
                f.Bitrate = (int)Jx.Long(o, "bitrate");
                f.ContentLength = Jx.Long(o, "contentLength");
                f.InitStart = Jx.Long(o, "initRange", "start"); f.InitEnd = Jx.Long(o, "initRange", "end");
                f.IndexStart = Jx.Long(o, "indexRange", "start"); f.IndexEnd = Jx.Long(o, "indexRange", "end");
                f.QualityLabel = Jx.Str(o, "qualityLabel");
                f.AudioTrackId = Jx.Str(o, "audioTrack", "id");
                f.AudioTrackName = Jx.Str(o, "audioTrack", "displayName");
                f.IsDefaultAudio = Jx.Bool(o, "audioTrack", "audioIsDefault");
                if (f.IsVideo) si.Video.Add(f); else si.Audio.Add(f);
            }
            foreach (object o in Jx.L(Jx.Get(root, "streamingData", "formats")))
            {
                StreamFormat f = new StreamFormat();
                f.Url = Jx.Str(o, "url");
                if (f.Url.Length == 0) continue;
                f.Itag = (int)Jx.Long(o, "itag");
                SplitMime(Jx.Str(o, "mimeType"), f);
                f.Width = (int)Jx.Long(o, "width"); f.Height = (int)Jx.Long(o, "height"); f.Fps = (int)Jx.Long(o, "fps");
                f.Bitrate = (int)Jx.Long(o, "bitrate");
                si.Muxed.Add(f);
            }
            foreach (object o in Jx.L(Jx.Get(root, "captions", "playerCaptionsTracklistRenderer", "captionTracks")))
            {
                CaptionTrack c = new CaptionTrack();
                c.Url = Jx.Str(o, "baseUrl");
                if (c.Url.Length == 0) continue;
                c.Lang = Jx.Str(o, "languageCode");
                c.Label = Jx.Text(Jx.Get(o, "name"));
                if (c.Label.Length == 0) c.Label = c.Lang;
                c.IsAuto = Jx.Str(o, "kind") == "asr";
                si.Captions.Add(c);
            }
            return si;
        }

        static StreamInfo ParseInvidiousStreams(object root, string videoId, string baseUrl)
        {
            StreamInfo si = new StreamInfo();
            si.VideoId = videoId; si.Source = "invidious";
            si.Title = Jx.Str(root, "title"); si.Author = Jx.Str(root, "author"); si.AuthorId = Jx.Str(root, "authorId");
            si.Duration = (int)Jx.Long(root, "lengthSeconds");
            si.IsLive = Jx.Bool(root, "liveNow");
            string hls = Jx.Str(root, "hlsUrl");
            if (hls.StartsWith("/", StringComparison.Ordinal)) hls = baseUrl + hls;
            si.HlsUrl = hls;
            foreach (object o in Jx.L(Jx.Get(root, "adaptiveFormats")))
            {
                StreamFormat f = new StreamFormat();
                f.Url = Jx.Str(o, "url");
                if (f.Url.Length == 0) continue;
                if (f.Url.StartsWith("/", StringComparison.Ordinal)) f.Url = baseUrl + f.Url;
                f.Itag = (int)Jx.Long(o, "itag");
                SplitMime(Jx.Str(o, "type"), f);
                f.Bitrate = (int)Jx.Long(o, "bitrate");
                f.ContentLength = Jx.Long(o, "clen");
                f.Fps = (int)Jx.Long(o, "fps");
                string size = Jx.Str(o, "size");
                int x = size.IndexOf('x');
                if (x > 0) { f.Width = int.Parse(size.Substring(0, x)); f.Height = int.Parse(size.Substring(x + 1)); }
                string init = Jx.Str(o, "init"), idx = Jx.Str(o, "index");
                long ra, rb;
                ParseRange(init, out ra, out rb); f.InitStart = ra; f.InitEnd = rb;
                ParseRange(idx, out ra, out rb); f.IndexStart = ra; f.IndexEnd = rb;
                f.QualityLabel = Jx.Str(o, "qualityLabel");
                f.AudioTrackId = Jx.Str(o, "audioTrack", "id");
                f.AudioTrackName = Jx.Str(o, "audioTrack", "displayName");
                f.IsDefaultAudio = Jx.Bool(o, "audioTrack", "audioIsDefault");
                if (f.IsVideo) si.Video.Add(f); else si.Audio.Add(f);
            }
            foreach (object o in Jx.L(Jx.Get(root, "captions")))
            {
                CaptionTrack c = new CaptionTrack();
                c.Lang = Jx.Str(o, "language_code"); c.Label = Jx.Str(o, "label");
                string u = Jx.Str(o, "url");
                if (u.StartsWith("/", StringComparison.Ordinal)) u = baseUrl + u;
                c.Url = u;
                if (c.Url.Length > 0) si.Captions.Add(c);
            }
            return si;
        }

        static void ParseRange(string s, out long a, out long b)
        {
            a = 0; b = 0;
            if (string.IsNullOrEmpty(s)) return;
            string[] p = s.Split('-');
            if (p.Length != 2) return;
            long.TryParse(p[0], out a); long.TryParse(p[1], out b);
        }

        // ---------------------------------------------------------- Captions
        public static Task<List<CaptionTrack>> Captions(string videoId)
        {
            return Task.Run(() =>
            {
                try
                {
                    Result<StreamInfo> r = GetStreamsSync(videoId);
                    if (r.Ok) return r.Value.Captions;
                }
                catch { }
                return new List<CaptionTrack>();
            });
        }

        /// <summary>Fetch a caption track as WebVTT (server side use).</summary>
        public static string FetchCaptionVtt(string baseUrl)
        {
            string u = baseUrl;
            if (u.IndexOf("fmt=", StringComparison.Ordinal) < 0) u += (u.IndexOf('?') >= 0 ? "&" : "?") + "fmt=vtt";
            else u = Regex.Replace(u, "fmt=[^&]*", "fmt=vtt");
            return Http.Send("GET", u, null, null, 12000);
        }

        // ---------------------------------------------------------- SponsorBlock
        public static Task<List<SponsorSegment>> SponsorSegments(string videoId)
        {
            return Task.Run(() => SponsorSegmentsSync(videoId));
        }

        public static List<SponsorSegment> SponsorSegmentsSync(string videoId)
        {
            List<SponsorSegment> res = new List<SponsorSegment>();
            try
            {
                string url = "https://sponsor.ajay.app/api/skipSegments?videoID=" + Uri.EscapeDataString(videoId) + "&categories=" + Uri.EscapeDataString("[\"sponsor\",\"selfpromo\"]");
                string json = Http.Get(url, 6000);
                foreach (object o in Jx.L(Jx.Parse(json)))
                {
                    string action = Jx.Str(o, "actionType");
                    if (action.Length > 0 && action != "skip") continue;
                    SponsorSegment s = new SponsorSegment();
                    s.Start = Jx.Dbl(o, "segment", "0");
                    s.End = Jx.Dbl(o, "segment", "1");
                    s.Category = Jx.Str(o, "category");
                    if (s.End > s.Start) res.Add(s);
                }
            }
            catch { }
            return res;
        }
    }
}
