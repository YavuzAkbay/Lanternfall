// Self-contained reader for Steam's keyless ISteamNews/GetNewsForApp/v2 API (used by GameNews for the selected game's
// patch notes / dev updates on the Spotlight home). No Playnite SDK, WPF or project references on purpose: the offline
// suite compiles this file on its own.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Selene
{
    internal class SteamNewsArticle
    {
        public string Title;
        public string Url;
        public string Source;
        public string Summary;
        public string Thumbnail;
        public DateTime Published;
        public int FeedType;
    }

    internal static class SteamNewsClient
    {
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // Steam's news feed carries articles in many languages; keep the English ones only (a Russian source was showing up).
        private static readonly Regex NonLatin = new Regex(
            "[\u0400-\u052F\u2DE0-\u2DFF\uA640-\uA69F\u3000-\u9FFF\u3040-\u30FF\uAC00-\uD7AF\u0600-\u06FF\u0590-\u05FF\u0370-\u03FF]",
            RegexOptions.Compiled);

        // appnews.newsitems[]: keep only items with a title and a safe http(s) link, strip their BBCode/HTML contents to
        // a short plain summary, and rank community announcements ahead of other posts (newest first within each group).
        public static List<SteamNewsArticle> Parse(string json)
        {
            var result = new List<SteamNewsArticle>();
            if (string.IsNullOrWhiteSpace(json)) return result;
            try
            {
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var root = serializer.DeserializeObject(json) as Dictionary<string, object>;
                object appObject;
                var appNews = root != null && root.TryGetValue("appnews", out appObject) ? appObject as Dictionary<string, object> : null;
                object itemsObject;
                var items = appNews != null && appNews.TryGetValue("newsitems", out itemsObject) ? itemsObject as object[] : null;
                if (items == null) return result;

                var ranked = new List<KeyValuePair<bool, SteamNewsArticle>>();
                foreach (var raw in items)
                {
                    var item = raw as Dictionary<string, object>;
                    if (item == null) continue;
                    string title = Str(item, "title"), url = Str(item, "url");
                    if (string.IsNullOrWhiteSpace(title) || !ValidUrl(url)) continue;
                    string label = Str(item, "feedlabel"), feed = Str(item, "feedname");
                    if (NonLatin.IsMatch(title) || NonLatin.IsMatch(label) || NonLatin.IsMatch(feed)) continue;
                    int feedType = Int(item, "feedtype");
                    bool announcement = feedType == 1
                        || label.IndexOf("Community Announcements", StringComparison.OrdinalIgnoreCase) >= 0
                        || string.Equals(feed, "steam_community_announcements", StringComparison.OrdinalIgnoreCase);

                    var article = new SteamNewsArticle();
                    article.Title = title.Trim();
                    article.Url = url;
                    article.Source = label.Length > 0 ? label : (feed.Length > 0 ? feed : "Steam");
                    string contents = Str(item, "contents");
                    article.Summary = Short(StripHtml(contents), 300);
                    article.Thumbnail = ImageFrom(contents, Str(item, "thumbnail"));
                    article.Published = FromUnix(Num(item, "date"));
                    article.FeedType = feedType;
                    ranked.Add(new KeyValuePair<bool, SteamNewsArticle>(announcement, article));
                }
                // an announcement ranks first only while it is about as recent as the newest post: a dormant game's old notice
                // must not hide the newer ones (an announcement more than 120 days older than the newest item is just a post)
                DateTime newest = ranked.Count == 0 ? DateTime.MinValue : ranked.Max(p => p.Value.Published);
                result.AddRange(ranked.OrderByDescending(p => p.Key && (newest - p.Value.Published).TotalDays <= 120)
                    .ThenByDescending(p => p.Value.Published).Select(p => p.Value));
            }
            catch (Exception)
            {
                // malformed JSON or an unexpected shape: no news is better than broken tiles
            }
            return result;
        }

        // the public, keyless endpoint. maxlength=0 is important: the article's picture lives in its body and a short
        // maxlength cut it off, leaving every tile without a thumbnail.
        public static string Url(string appId, int count)
        {
            return "https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/?appid="
                + Uri.EscapeDataString(appId ?? "") + "&count=" + count + "&maxlength=0";
        }

        // Steam rarely fills `thumbnail`; the picture is usually the first [img] / <img> in the body (or a preview video).
        internal static string ImageFrom(string contents, string thumbnail)
        {
            var url = ImageUrl(thumbnail);
            if (url != null) return url;
            if (string.IsNullOrEmpty(contents)) return null;
            var bb = Regex.Match(contents, @"\[img\]\s*(.+?)\s*\[/img\]", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (bb.Success) { url = ImageUrl(Expand(bb.Groups[1].Value)); if (url != null) return url; }
            var html = Regex.Match(contents, @"<img[^>]*?src\s*=\s*(?:""([^""]+)""|'([^']+)'|([^\s>]+))", RegexOptions.IgnoreCase);
            if (html.Success)
            {
                for (int group = 1; group <= 3; group++)
                    if (html.Groups[group].Success) { url = ImageUrl(Expand(html.Groups[group].Value)); if (url != null) return url; }
            }
            var video = Regex.Match(contents, @"\[previewyoutube=([^;\]]+)", RegexOptions.IgnoreCase);
            if (video.Success) return ImageUrl("https://i.ytimg.com/vi/" + video.Groups[1].Value + "/hqdefault.jpg");
            return null;
        }

        private static string Expand(string value)
        {
            // the token is followed by a path starting with '/' in Steam's BBCode
            return (value ?? "").Replace("{STEAM_CLAN_IMAGE}", "https://clan.cloudflare.steamstatic.com/images").Trim();
        }

        private static string ImageUrl(string value)
        {
            if (!ValidUrl(value)) return null;
            // NewsCard.Picture only accepts JPEG/PNG/GIF/BMP; a webp would fail and leave the tile blank
            if (value.IndexOf(".webp", StringComparison.OrdinalIgnoreCase) >= 0) return null;
            return value;
        }

        public static string Fetch(string appId, int count)
        {
            var request = (HttpWebRequest)WebRequest.Create(Url(appId, count));
            request.Method = "GET";
            request.Timeout = 10000;
            request.ReadWriteTimeout = 10000;
            request.UserAgent = "Selene game news reader";
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        // gate for links that get opened and pictures that get downloaded: http(s) only, no credentials, and no
        // host that is obviously this PC or the home network (an article body is written by the game's publisher)
        public static bool ValidUrl(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            for (int i = 0; i < value.Length; i++) if (char.IsControl(value[i])) return false;
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
            if (!string.IsNullOrEmpty(uri.UserInfo)) return false;
            return PublicHost(uri);
        }

        internal static bool PublicHost(Uri uri)
        {
            string host = (uri.Host ?? "").Trim('[', ']').ToLowerInvariant();
            if (host.Length == 0 || host == "localhost" || host.EndsWith(".localhost") || host.EndsWith(".local") || host.EndsWith(".internal")) return false;
            IPAddress ip;
            if (IPAddress.TryParse(host, out ip))
            {
                if (IPAddress.IsLoopback(ip)) return false;
                var b = ip.GetAddressBytes();
                if (b.Length == 4)
                {
                    if (b[0] == 10 || b[0] == 0 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254)) return false;
                }
                else if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (b.Length == 16 && (b[0] & 0xFE) == 0xFC)) return false;
            }
            return true;
        }

        internal static string StripHtml(string html)
        {
            if (string.IsNullOrEmpty(html)) return "";
            string decoded = WebUtility.HtmlDecode(html);
            var plain = new StringBuilder(decoded.Length);
            bool inTag = false;
            for (int i = 0; i < decoded.Length; i++)
            {
                char c = decoded[i];
                if (c == '<') { inTag = true; continue; }
                if (c == '>' && inTag) { inTag = false; plain.Append(' '); continue; }
                if (!inTag) plain.Append(c);
            }
            var collapsed = new StringBuilder(plain.Length);
            bool space = false;
            for (int i = 0; i < plain.Length; i++)
            {
                char c = plain[i];
                if (char.IsWhiteSpace(c) || c == '\u00a0')
                {
                    if (!space && collapsed.Length > 0) { collapsed.Append(' '); space = true; }
                }
                else { collapsed.Append(c); space = false; }
            }
            return collapsed.ToString().Trim();
        }

        private static string Short(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Length <= max ? text : text.Substring(0, max - 1).TrimEnd() + "\u2026";
        }

        private static DateTime FromUnix(long seconds)
        {
            try { return Epoch.AddSeconds(seconds); } catch (Exception) { return DateTime.MinValue; }
        }

        private static string Str(Dictionary<string, object> item, string key)
        {
            object value;
            return item != null && item.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : "";
        }

        private static long Num(Dictionary<string, object> item, string key)
        {
            object value;
            if (item == null || !item.TryGetValue(key, out value) || value == null) return 0;
            try { return Convert.ToInt64(value); } catch (Exception) { return 0; }
        }

        private static int Int(Dictionary<string, object> item, string key)
        {
            object value;
            if (item == null || !item.TryGetValue(key, out value) || value == null) return 0;
            try { return Convert.ToInt32(value); } catch (Exception) { return 0; }
        }
    }
}
