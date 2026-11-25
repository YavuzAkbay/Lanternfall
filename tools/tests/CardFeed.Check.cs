// Offline checks for the Browser / YouTube card tiles. Compiles only Browser\CardFeed.cs with these small stubs,
// so no WPF, WebView2 or Playnite assemblies are needed (see cardfeed.ps1).
using System;
using System.Collections.Generic;
using System.Linq;

namespace SeleneBrowser
{
    internal class VideoItem
    {
        public string Id;
        public string Title;
        public string Author;
        public long Published;
        public bool IsShort;
    }

    internal class BrowserEntry
    {
        public string Title;
        public string Url;
        public long Time;
    }

    internal static class CardFeedCheck
    {
        private static int failures;

        private static void Check(bool ok, string name)
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
            if (!ok) failures++;
        }

        private static long Now() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds; }

        private static int Main()
        {
            long now = Now();
            var feed = new List<VideoItem>
            {
                new VideoItem { Id = "aaaaaaaaaaa", Title = "Old video", Author = "Channel A", Published = now - 100000 },
                new VideoItem { Id = "bbbbbbbbbbb", Title = "Newest video", Author = "Channel B", Published = now - 100, IsShort = true },
                new VideoItem { Id = "ccccccccccc", Title = "Middle video", Author = "Channel C", Published = now - 5000 },
                new VideoItem { Id = "", Title = "No id", Published = now },
                new VideoItem { Id = "ddddddddddd", Title = "   ", Published = now },
                new VideoItem { Id = "eeeeeeeeeee", Title = "Fourth video", Published = now - 200000 }
            };
            var yt = CardFeed.YouTube(feed, now - 10000, 3, true);
            Check(yt.Count == 3, "newest three videos");
            Check(yt[0].Key == "bbbbbbbbbbb" && yt[1].Key == "ccccccccccc" && yt[2].Key == "aaaaaaaaaaa", "newest first, invalid skipped");
            Check(yt[0].Title == "Newest video", "video title");
            Check(yt[0].Sub.StartsWith("Channel B \u00b7 "), "channel and age");
            Check(yt[0].IsNew && yt[2].IsNew == false, "NEW only for videos after the seen mark");
            Check(yt[0].Sub.Contains("NEW") && !yt[2].Sub.Contains("NEW"), "NEW badge in subtitle");
            Check(yt[0].ImageUrl == "https://i.ytimg.com/vi/bbbbbbbbbbb/mqdefault.jpg", "thumbnail url");
            Check(CardFeed.YouTube(null, 0, 3, true).Count == 0, "null feed is empty");
            var noShorts = CardFeed.YouTube(feed, now - 10000, 3, false);
            Check(noShorts.Count == 3 && noShorts.All(x => x.Key != "bbbbbbbbbbb"), "Shorts skipped when switched off");
            Check(noShorts[0].Key == "ccccccccccc", "videos fill the freed tile");

            var marks = new List<BrowserEntry>
            {
                new BrowserEntry { Title = "GitHub", Url = "https://github.com/", Time = now - 50 },
                new BrowserEntry { Title = "Wikipedia", Url = "https://en.wikipedia.org/wiki/Main_Page", Time = now - 60 },
                new BrowserEntry { Title = "Bad", Url = "file:///C:/secret.txt", Time = now }
            };
            var recent = new List<BrowserEntry>
            {
                new BrowserEntry { Title = "Github again", Url = "https://GITHUB.com/", Time = now },
                new BrowserEntry { Title = "Some article", Url = "https://news.example.com/story", Time = now - 120 },
                new BrowserEntry { Title = "https://bare.example.com/", Url = "https://bare.example.com/", Time = now - 130 }
            };
            var br = CardFeed.Browser(marks, recent, 3);
            Check(br.Count == 3, "three site tiles");
            Check(br[0].Title == "GitHub" && br[1].Title == "Wikipedia", "bookmarks before recent pages");
            Check(br[2].Title == "Some article", "recent pages fill the rest");
            Check(br.All(x => !x.Key.Contains("GITHUB.com")), "duplicate site skipped");
            Check(br[0].Host == "github.com" && br[1].Host == "en.wikipedia.org", "host without www");
            Check(br[0].Sub.StartsWith("github.com \u00b7 "), "site subtitle");
            Check(br[2].Sub.Contains("Just now") || br[2].Sub.Contains("min ago"), "recent time in subtitle");
            Check(CardFeed.Browser(null, null, 3).Count == 0, "empty browser data");
            Check(CardFeed.Host("javascript:alert(1)") == null, "unsafe url refused");
            Check(CardFeed.Short("A very long title that must be trimmed", 12) == "A very long\u2026", "ellipsis");

            Console.WriteLine(failures == 0 ? "All card feed checks passed." : failures + " card feed check(s) failed.");
            return failures == 0 ? 0 : 1;
        }
    }
}
