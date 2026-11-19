// Nomad YouTube data models + small helpers (C# 5 only).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace SeleneBrowser
{
    public class VideoItem
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Author { get; set; }
        public string AuthorId { get; set; }
        public int Duration { get; set; }          // seconds, 0 = unknown/live
        public string DurationText { get; set; }   // "12:34"
        public string Thumbnail { get; set; }
        public long Published { get; set; }        // unix seconds, 0 = unknown
        public string Age { get; set; }            // "3 days ago"
        public string Views { get; set; }          // "1.2M views"
        public bool IsLive { get; set; }
        public bool IsShort { get; set; }
        public double WatchProgress { get; set; }  // seconds already watched
        public long WatchedAt { get; set; }        // unix seconds (history only)

        public VideoItem()
        {
            Id = ""; Title = ""; Author = ""; AuthorId = ""; DurationText = ""; Thumbnail = ""; Age = ""; Views = "";
        }
    }

    public class ChannelItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Thumbnail { get; set; }   // avatar
        public string Banner { get; set; }
        public string Subscribers { get; set; }
        public string Description { get; set; }
        public ChannelItem() { Id = ""; Name = ""; Thumbnail = ""; Banner = ""; Subscribers = ""; Description = ""; }
    }

    public class PlaylistItem
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Author { get; set; }
        public int VideoCount { get; set; }
        public string Thumbnail { get; set; }
        public List<VideoItem> Videos { get; set; }
        public PlaylistItem() { Id = ""; Title = ""; Author = ""; Thumbnail = ""; Videos = new List<VideoItem>(); }
    }

    public class CaptionTrack
    {
        public string Lang { get; set; }
        public string Label { get; set; }
        public string Url { get; set; }       // upstream URL (server side only)
        public bool IsAuto { get; set; }
        public string Token { get; set; }     // LocalServer token -> /cc/<token>.vtt
        public CaptionTrack() { Lang = ""; Label = ""; Url = ""; Token = ""; }
    }

    public class StreamFormat
    {
        public int Itag { get; set; }
        public string Mime { get; set; }          // full mime string as YouTube returns it
        public string MimeBase { get; set; }      // video/webm
        public string Codecs { get; set; }        // vp09.00.40.08
        public int Width { get; set; }
        public int Height { get; set; }
        public int Fps { get; set; }
        public int Bitrate { get; set; }
        public string Url { get; set; }
        public long InitStart { get; set; }
        public long InitEnd { get; set; }
        public long IndexStart { get; set; }
        public long IndexEnd { get; set; }
        public long ContentLength { get; set; }
        public bool IsVideo { get; set; }
        public string QualityLabel { get; set; }
        public string AudioTrackId { get; set; }  // "en.4" or ""
        public string AudioTrackName { get; set; }
        public bool IsDefaultAudio { get; set; }
        public string Token { get; set; }         // LocalServer token
        public StreamFormat() { Mime = ""; MimeBase = ""; Codecs = ""; Url = ""; QualityLabel = ""; AudioTrackId = ""; AudioTrackName = ""; Token = ""; }

        public string AudioLang
        {
            get
            {
                if (string.IsNullOrEmpty(AudioTrackId)) return "und";
                int i = AudioTrackId.IndexOf('.');
                string l = i > 0 ? AudioTrackId.Substring(0, i) : AudioTrackId;
                return string.IsNullOrEmpty(l) ? "und" : l;
            }
        }
    }

    public class StreamInfo
    {
        public string VideoId { get; set; }
        public string Title { get; set; }
        public string Author { get; set; }
        public string AuthorId { get; set; }
        public int Duration { get; set; }
        public bool IsLive { get; set; }
        public string Source { get; set; }   // "innertube" | "invidious"
        public string HlsUrl { get; set; }
        public string HlsToken { get; set; }   // LocalServer token of the master playlist -> /hls/<token>.m3u8
        public List<StreamFormat> Video { get; set; }
        public List<StreamFormat> Audio { get; set; }
        public List<StreamFormat> Muxed { get; set; }
        public List<CaptionTrack> Captions { get; set; }
        // live segment path (InnerTube IOS client): self-initialising 5 s segments reachable as <url>&sq=N
        public List<StreamFormat> LiveVideo { get; set; }
        public StreamFormat LiveAudio { get; set; }
        public int SegmentSeconds { get; set; }
        public StreamInfo()
        {
            LiveVideo = new List<StreamFormat>(); SegmentSeconds = 5;
            VideoId = ""; Title = ""; Author = ""; AuthorId = ""; Source = ""; HlsUrl = ""; HlsToken = "";
            Video = new List<StreamFormat>(); Audio = new List<StreamFormat>(); Muxed = new List<StreamFormat>(); Captions = new List<CaptionTrack>();
        }
    }

    public class SponsorSegment
    {
        public double Start { get; set; }
        public double End { get; set; }
        public string Category { get; set; }
        public SponsorSegment() { Category = ""; }
    }

    /// <summary>One page of results. Never null from the API; check Ok / Error.</summary>
    public class PageResult<T>
    {
        public List<T> Items { get; set; }
        public string Continuation { get; set; }   // null/"" = no more
        public string Error { get; set; }          // human readable, null/"" = success
        public string Source { get; set; }         // which backend answered
        public ChannelItem Header { get; set; }    // channel pages
        public List<ChannelItem> Channels { get; set; }
        public List<PlaylistItem> Playlists { get; set; }
        public PageResult() { Items = new List<T>(); Continuation = ""; Error = ""; Source = ""; Channels = new List<ChannelItem>(); Playlists = new List<PlaylistItem>(); }
        public bool Ok { get { return string.IsNullOrEmpty(Error); } }
        public bool HasMore { get { return !string.IsNullOrEmpty(Continuation); } }
        public static PageResult<T> Fail(string msg) { PageResult<T> r = new PageResult<T>(); r.Error = msg; return r; }
    }

    public class Result<T>
    {
        public T Value { get; set; }
        public string Error { get; set; }
        public Result() { Error = ""; }
        public bool Ok { get { return string.IsNullOrEmpty(Error); } }
        public static Result<T> Fail(string msg) { Result<T> r = new Result<T>(); r.Error = msg; return r; }
        public static Result<T> Success(T v) { Result<T> r = new Result<T>(); r.Value = v; return r; }
    }

    public class FreeTubeSettings
    {
        public int DefaultQuality { get; set; }     // 0 = auto/unknown
        public bool UseSponsorBlock { get; set; }
    }

    // ---------------------------------------------------------------- formatting helpers
    public static class Fmt
    {
        public static string Thumb(string id) { return "https://i.ytimg.com/vi/" + id + "/mqdefault.jpg"; }

        public static long NowUnix() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds; }

        public static string Duration(int sec)
        {
            if (sec <= 0) return "";
            int h = sec / 3600, m = (sec % 3600) / 60, s = sec % 60;
            if (h > 0) return h + ":" + m.ToString("00") + ":" + s.ToString("00");
            return m + ":" + s.ToString("00");
        }

        public static int ParseDuration(string t)
        {
            if (string.IsNullOrEmpty(t)) return 0;
            t = t.Trim();
            if (!Regex.IsMatch(t, @"^\d+(:\d{1,2}){1,2}$")) return 0;
            string[] p = t.Split(':');
            int total = 0;
            foreach (string x in p) total = total * 60 + int.Parse(x, CultureInfo.InvariantCulture);
            return total;
        }

        public static string Views(long n)
        {
            if (n < 0) return "";
            string s;
            if (n >= 1000000000L) s = (n / 1e9).ToString("0.#", CultureInfo.InvariantCulture) + "B";
            else if (n >= 1000000L) s = (n / 1e6).ToString("0.#", CultureInfo.InvariantCulture) + "M";
            else if (n >= 1000L) s = (n / 1e3).ToString("0.#", CultureInfo.InvariantCulture) + "K";
            else s = n.ToString(CultureInfo.InvariantCulture);
            return s + (n == 1 ? " view" : " views");
        }

        public static string Age(long unix)
        {
            if (unix <= 0) return "";
            long d = Fmt.NowUnix() - unix;
            if (d < 0) d = 0;
            if (d < 60) return "just now";
            if (d < 3600) return Plural(d / 60, "minute");
            if (d < 86400) return Plural(d / 3600, "hour");
            if (d < 86400L * 7) return Plural(d / 86400, "day");
            if (d < 86400L * 30) return Plural(d / (86400L * 7), "week");
            if (d < 86400L * 365) return Plural(d / (86400L * 30), "month");
            return Plural(d / (86400L * 365), "year");
        }

        static string Plural(long n, string unit) { return n + " " + unit + (n == 1 ? "" : "s") + " ago"; }

        /// <summary>"3 days ago" -> approximate unix seconds (0 if not parsable).</summary>
        public static long ParseAge(string age)
        {
            if (string.IsNullOrEmpty(age)) return 0;
            Match m = Regex.Match(age, @"(\d+)\s*(second|minute|hour|day|week|month|year)", RegexOptions.IgnoreCase);
            if (!m.Success) return 0;
            long n = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            long u;
            switch (m.Groups[2].Value.ToLowerInvariant())
            {
                case "second": u = 1; break;
                case "minute": u = 60; break;
                case "hour": u = 3600; break;
                case "day": u = 86400; break;
                case "week": u = 86400L * 7; break;
                case "month": u = 86400L * 30; break;
                default: u = 86400L * 365; break;
            }
            return Fmt.NowUnix() - n * u;
        }

        public static string Decode(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return System.Net.WebUtility.HtmlDecode(s);
        }
    }

    // ---------------------------------------------------------------- tiny JSON navigation helpers
    internal static class Jx
    {
        public static object Parse(string s)
        {
            JavaScriptSerializer ser = new JavaScriptSerializer();
            ser.MaxJsonLength = int.MaxValue;
            ser.RecursionLimit = 4000;
            return ser.DeserializeObject(s);
        }

        public static string ToJson(object o)
        {
            JavaScriptSerializer ser = new JavaScriptSerializer();
            ser.MaxJsonLength = int.MaxValue;
            ser.RecursionLimit = 4000;
            return ser.Serialize(o);
        }

        public static Dictionary<string, object> D(object o) { return o as Dictionary<string, object>; }

        public static IList L(object o)
        {
            IList l = o as IList;
            return l != null ? l : new object[0];
        }

        public static object Get(object o, params string[] path)
        {
            object cur = o;
            for (int i = 0; i < path.Length; i++)
            {
                if (cur == null) return null;
                Dictionary<string, object> d = cur as Dictionary<string, object>;
                if (d != null)
                {
                    object v;
                    if (!d.TryGetValue(path[i], out v)) return null;
                    cur = v;
                    continue;
                }
                IList l = cur as IList;
                if (l != null)
                {
                    int idx;
                    if (path[i] == "last") idx = l.Count - 1;
                    else if (!int.TryParse(path[i], out idx)) return null;
                    if (idx < 0 || idx >= l.Count) return null;
                    cur = l[idx];
                    continue;
                }
                return null;
            }
            return cur;
        }

        public static string Str(object o, params string[] path)
        {
            object v = Get(o, path);
            if (v == null) return "";
            string s = v as string;
            if (s != null) return s;
            if (v is IDictionary || v is IList) return "";
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static long Long(object o, params string[] path)
        {
            object v = Get(o, path);
            if (v == null) return 0;
            try
            {
                string s = v as string;
                if (s != null)
                {
                    long r;
                    if (long.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out r)) return r;
                    double dd;
                    if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out dd)) return (long)dd;
                    return 0;
                }
                if (v is IDictionary || v is IList) return 0;
                return Convert.ToInt64(Convert.ToDouble(v, CultureInfo.InvariantCulture));
            }
            catch { return 0; }
        }

        public static double Dbl(object o, params string[] path)
        {
            object v = Get(o, path);
            if (v == null) return 0;
            try
            {
                string s = v as string;
                if (s != null)
                {
                    double r;
                    return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out r) ? r : 0;
                }
                if (v is IDictionary || v is IList) return 0;
                return Convert.ToDouble(v, CultureInfo.InvariantCulture);
            }
            catch { return 0; }
        }

        public static bool Bool(object o, params string[] path)
        {
            object v = Get(o, path);
            if (v is bool) return (bool)v;
            string s = v as string;
            return s != null && s.ToLowerInvariant() == "true";
        }

        /// <summary>Text from {simpleText}, {runs:[{text}]}, {content}, plain string.</summary>
        public static string Text(object o)
        {
            if (o == null) return "";
            string s = o as string;
            if (s != null) return s;
            Dictionary<string, object> d = o as Dictionary<string, object>;
            if (d == null) return "";
            object v;
            if (d.TryGetValue("simpleText", out v) && v is string) return (string)v;
            if (d.TryGetValue("content", out v) && v is string) return (string)v;
            if (d.TryGetValue("runs", out v))
            {
                StringBuilder sb = new StringBuilder();
                foreach (object r in L(v)) sb.Append(Str(r, "text"));
                return sb.ToString();
            }
            if (d.TryGetValue("text", out v)) return Text(v);
            return "";
        }

        /// <summary>Depth-first, document order. Hits are collected and NOT descended into.</summary>
        public static void Collect(object node, ICollection<string> keys, List<KeyValuePair<string, object>> into)
        {
            Dictionary<string, object> d = node as Dictionary<string, object>;
            if (d != null)
            {
                foreach (KeyValuePair<string, object> kv in d)
                {
                    if (keys.Contains(kv.Key)) into.Add(new KeyValuePair<string, object>(kv.Key, kv.Value));
                    else Collect(kv.Value, keys, into);
                }
                return;
            }
            IList l = node as IList;
            if (l != null) foreach (object x in l) Collect(x, keys, into);
        }

        /// <summary>First string value under the given key anywhere below node (optionally with prefix).</summary>
        public static string FindString(object node, string key, string prefix)
        {
            Dictionary<string, object> d = node as Dictionary<string, object>;
            if (d != null)
            {
                foreach (KeyValuePair<string, object> kv in d)
                {
                    string s = kv.Value as string;
                    if (kv.Key == key && s != null && (prefix == null || s.StartsWith(prefix, StringComparison.Ordinal))) return s;
                    string r = FindString(kv.Value, key, prefix);
                    if (r != null) return r;
                }
                return null;
            }
            IList l = node as IList;
            if (l != null)
            {
                foreach (object x in l)
                {
                    string r = FindString(x, key, prefix);
                    if (r != null) return r;
                }
            }
            return null;
        }
    }
}
