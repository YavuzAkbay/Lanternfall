// CardFeed.cs - shapes the data for the three tiles under the Browser / YouTube Spotlight cards.
// This file is deliberately free of WPF and I/O so tools\tests\CardFeed.Check.cs can compile it on its own:
// YouTube reads the already cached subscription feed, the Browser card reads its own bookmarks + history.
using System;
using System.Collections.Generic;
using System.Linq;

namespace SeleneBrowser
{
    internal class FeedCard
    {
        public string Title;
        public string Sub;
        public string Key;       // video id, or the site's URL
        public string ImageUrl;  // YouTube thumbnail; null for locally drawn site tiles
        public string Host;      // site tiles only
        public bool IsNew;
    }

    internal static class CardFeed
    {
        public const int Tiles = 3;

        // Newest videos from the subscribed channels (the feed is already filtered to subscribed channel IDs).
        // includeShorts follows the YouTube screen's own Shorts switch, so hiding them there hides them here too.
        public static List<FeedCard> YouTube(IEnumerable<VideoItem> feed, long seen, int max, bool includeShorts)
        {
            var result = new List<FeedCard>();
            if (feed == null) return result;
            foreach (var v in feed.Where(x => x != null && !string.IsNullOrEmpty(x.Id) && !string.IsNullOrWhiteSpace(x.Title)
                                              && (includeShorts || !x.IsShort))
                                  .OrderByDescending(x => x.Published)
                                  .Take(Math.Max(0, max)))
            {
                string sub = "";
                if (!string.IsNullOrWhiteSpace(v.Author)) sub = Short(v.Author, 24) + " \u00b7 ";
                sub += Relative(v.Published);
                if (seen > 0 && v.Published > seen) sub += " \u00b7 NEW";
                result.Add(new FeedCard
                {
                    Title = Short(v.Title, 44),
                    Sub = sub,
                    Key = v.Id,
                    ImageUrl = Thumb(v.Id),
                    IsNew = seen > 0 && v.Published > seen
                });
            }
            return result;
        }

        // The user's own places first, then recent pages to fill the remaining tiles (no web favicons or screenshots).
        public static List<FeedCard> Browser(IEnumerable<BrowserEntry> bookmarks, IEnumerable<BrowserEntry> recent, int max)
        {
            var result = new List<FeedCard>();
            var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int limit = Math.Max(0, max);
            var groups = new[] { bookmarks, recent };
            for (int g = 0; g < groups.Length; g++)
            {
                var group = groups[g];
                if (group == null) continue;
                foreach (var e in group)
                {
                    if (result.Count >= limit) break;
                    if (e == null) continue;
                    string host = Host(e.Url);
                    if (host == null) continue;
                    if (!seenUrls.Add(Normalize(e.Url))) continue;
                    result.Add(new FeedCard
                    {
                        Title = PageName(e, host),
                        // a bookmark's time is when it was saved (the starter set says "just now" forever): only visited pages show their age
                        Sub = g == 0 ? host + " · Bookmark" : host + Relative(e.Time),
                        Key = e.Url,
                        Host = host
                    });
                }
            }
            return result;
        }

        internal static string Thumb(string id)
        {
            return "https://i.ytimg.com/vi/" + (id ?? "") + "/mqdefault.jpg";
        }

        internal static string Host(string url)
        {
            Uri u;
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out u)) return null;
            if (!string.Equals(u.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(u.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return null;
            string host = u.Host ?? "";
            return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host.Substring(4) : host;
        }

        private static string Normalize(string url)
        {
            string s = (url ?? "").Trim().TrimEnd('/');
            return s.Length > 240 ? s.Substring(0, 240) : s;
        }

        private static string PageName(BrowserEntry e, string host)
        {
            string title = (e.Title ?? "").Trim();
            if (title.Length > 0 && !title.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return Short(title, 44);
            return Short(host, 44);
        }

        internal static string Short(string s, int max)
        {
            s = (s ?? "").Trim();
            return s.Length <= max ? s : s.Substring(0, max - 1).TrimEnd() + "\u2026";
        }

        internal static string Relative(long unix)
        {
            if (unix <= 0) return "";
            long now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            long ago = now - unix;
            if (ago < 0 || ago < 60) return " \u00b7 Just now";
            if (ago < 3600) return " \u00b7 " + (ago / 60) + " min ago";
            if (ago < 86400) return " \u00b7 " + (ago / 3600) + "h ago";
            if (ago < 604800) return " \u00b7 " + (ago / 86400) + "d ago";
            try
            {
                return " \u00b7 " + new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(unix).ToLocalTime().ToString("d MMM");
            }
            catch { return ""; }
        }
    }
}
