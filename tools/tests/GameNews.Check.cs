using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;

namespace Selene
{
    // Same shape as GameNews' on-disk cache entry: an internal class with public fields of the internal article type.
    internal class TestGameNewsCacheEntry
    {
        public long SavedTicks;
        public List<SteamNewsArticle> Articles;
    }

    // Offline checks for the self-contained Steam news reader: canned JSON only, no network, no SDK/WPF.
    class GameNewsCheck
    {
        const string SteamJson = @"{
          ""appnews"": {
            ""appid"": 570,
            ""newsitems"": [
              {
                ""gid"": ""1"",
                ""title"": ""Dota 2 Update - Patch 7.38"",
                ""url"": ""https://store.steampowered.com/news/app/570/view/1"",
                ""contents"": ""<p>Hello <b>world</b>&nbsp;! &amp; more</p><ul><li>One</li><li>Two</li></ul>"",
                ""feedlabel"": ""Community Announcements"",
                ""feedname"": ""steam_community_announcements"",
                ""feedtype"": 1,
                ""date"": 1700000000,
                ""thumbnail"": ""https://cdn.steamstatic.com/steamcommunity/public/images/apps/570/thumb.jpg""
              },
              {
                ""gid"": ""2"",
                ""title"": ""Dev diary: new heroes"",
                ""url"": ""https://www.dota2.com/newsentry/2"",
                ""contents"": ""A dev diary."",
                ""feedlabel"": ""Steam News"",
                ""feedname"": """",
                ""feedtype"": 0,
                ""date"": 1700864000,
                ""thumbnail"": """"
              },
              { ""title"": ""  "", ""url"": ""https://example.com/blank"", ""feedlabel"": ""Steam News"", ""feedtype"": 0, ""date"": 1900000000 },
              { ""title"": ""File trap"", ""url"": ""file:///C:/Windows/System32/calc.exe"", ""feedlabel"": ""Steam News"", ""feedtype"": 0, ""date"": 1900000000 },
              { ""title"": ""Relative trap"", ""url"": ""/news/local"", ""feedlabel"": ""Steam News"", ""feedtype"": 0, ""date"": 1900000000 },
              { ""title"": ""Credentials trap"", ""url"": ""https://user:pass@example.com/trap"", ""feedlabel"": ""Steam News"", ""feedtype"": 0, ""date"": 1900000000 }
            ]
          }
        }";

        const string RichJson = @"{ ""appnews"": { ""newsitems"": [
          { ""title"": ""Премьера Одиссеи Кристофера Нолана"", ""url"": ""https://example.com/ru"", ""feedlabel"": ""PlayGround.ru"", ""feedtype"": 0, ""date"": 1900000000 },
          { ""title"": ""Clan image post"", ""url"": ""https://example.com/clan"", ""feedlabel"": ""Community Announcements"", ""feedtype"": 1, ""date"": 1700000000,
            ""contents"": ""Hello [img]{STEAM_CLAN_IMAGE}/46094181/pic.jpg[/img] world"" },
          { ""title"": ""Html image post"", ""url"": ""https://example.com/html"", ""feedlabel"": ""PC Gamer"", ""feedtype"": 0, ""date"": 1600000000,
            ""contents"": ""<p><img src=\""https://cdn.example.com/news.png\"" /></p>"" },
          { ""title"": ""Video post"", ""url"": ""https://example.com/video"", ""feedlabel"": ""Steam News"", ""feedtype"": 0, ""date"": 1500000000,
            ""contents"": ""[previewyoutube=abc123XYZ;full][/previewyoutube]"" },
          { ""title"": ""Webp post"", ""url"": ""https://example.com/webp"", ""feedlabel"": ""Steam News"", ""feedtype"": 0, ""date"": 1400000000,
            ""thumbnail"": ""https://cdn.example.com/pic.webp"" }
        ] } }";

        const string LabelJson = @"{ ""appnews"": { ""newsitems"": [
          { ""title"": ""Newer regular post"", ""url"": ""https://example.com/new"", ""feedlabel"": ""Steam News"", ""feedtype"": 0, ""date"": 1700864000 },
          { ""title"": ""Older announcement by label"", ""url"": ""https://example.com/old"", ""feedlabel"": ""Community Announcements"", ""feedtype"": 0, ""date"": 1700000000 }
        ] } }";

        const string LongHtmlJson = @"{ ""appnews"": { ""newsitems"": [
          { ""title"": ""Long post"", ""url"": ""https://example.com/long"", ""contents"": ""<p>" + LongText + @"</p>"", ""feedtype"": 0, ""date"": 1700000000 }
        ] } }";

        const string LongText = "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx" +
            "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx" +
            "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx" +
            "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx";

        static int failures;

        static void Eq(object expected, object actual, string what)
        {
            if (!object.Equals(expected, actual))
                throw new Exception(what + ": expected [" + expected + "], got [" + actual + "]");
        }

        static void Yes(bool condition, string what)
        {
            if (!condition) throw new Exception(what);
        }

        static void Group(string name, Action check)
        {
            int before = failures;
            try { check(); }
            catch (Exception ex) { Console.Error.WriteLine("  " + ex.Message); failures++; }
            Console.WriteLine((failures == before ? "PASS " : "FAIL ") + name);
        }

        static int Main()
        {
            Group("ParseNews", delegate
            {
                var list = SteamNewsClient.Parse(SteamJson);
                Eq(2, list.Count, "blank titles and unsafe URLs are skipped");
                Eq("Dota 2 Update - Patch 7.38", list[0].Title, "announcement title");
                Eq("https://store.steampowered.com/news/app/570/view/1", list[0].Url, "article URL");
                Eq(new DateTime(2023, 11, 14, 22, 13, 20, DateTimeKind.Utc), list[0].Published, "Unix date in UTC");
                Eq(1, list[0].FeedType, "feed type");
                Eq("Community Announcements", list[0].Source, "source from feedlabel");
                Eq("https://cdn.steamstatic.com/steamcommunity/public/images/apps/570/thumb.jpg", list[0].Thumbnail, "thumbnail URL");
                Eq("Dev diary: new heroes", list[1].Title, "dev post after the announcement");
                Eq(null, list[1].Thumbnail, "blank thumbnail stays null");
                Eq(0, SteamNewsClient.Parse(null).Count, "null JSON");
                Eq(0, SteamNewsClient.Parse("{}").Count, "JSON with no appnews");
            });

            Group("AnnouncementFirst", delegate
            {
                var byType = SteamNewsClient.Parse(SteamJson);
                Yes(byType[0].FeedType == 1 && byType[1].FeedType == 0, "feedtype 1 ranks before a newer normal post");
                var byLabel = SteamNewsClient.Parse(LabelJson);
                Eq(2, byLabel.Count, "both labelled posts parsed");
                Eq("Older announcement by label", byLabel[0].Title, "Community Announcements label ranks first even at feedtype 0");
                Eq("Newer regular post", byLabel[1].Title, "newest first within the other group");
            });

            Group("StaleAnnouncement", delegate
            {
                // an announcement far older than the newest post is just an old post: it must not hide the newer ones
                const string stale = @"{ ""appnews"": { ""newsitems"": [
                  { ""title"": ""Old announcement"", ""url"": ""https://example.com/a"", ""feedlabel"": ""Community Announcements"", ""feedtype"": 1, ""date"": 1600000000 },
                  { ""title"": ""Fresh patch notes"", ""url"": ""https://example.com/b"", ""feedlabel"": ""Steam News"", ""feedtype"": 0, ""date"": 1700000000 }
                ] } }";
                var list = SteamNewsClient.Parse(stale);
                Eq("Fresh patch notes", list[0].Title, "a year-old announcement does not outrank a newer post");
            });

            Group("StripHtml", delegate
            {
                var list = SteamNewsClient.Parse(SteamJson);
                Eq("Hello world ! & more One Two", list[0].Summary, "tags removed, entities decoded, whitespace collapsed");
                Eq("", SteamNewsClient.StripHtml(null), "null HTML");
                var longSummary = SteamNewsClient.Parse(LongHtmlJson)[0].Summary;
                Eq(300, longSummary.Length, "summary truncated to ~300 chars");
                Yes(longSummary.EndsWith("\u2026"), "truncated summary ends with an ellipsis");
            });

            Group("Url", delegate
            {
                Eq("https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/?appid=570&count=3&maxlength=0",
                   SteamNewsClient.Url("570", 3), "keyless GetNewsForApp URL with count and full contents");
                var rich = SteamNewsClient.Parse(RichJson);
                Eq(4, rich.Count, "non-English article is skipped");
                Eq("https://clan.cloudflare.steamstatic.com/images/46094181/pic.jpg", rich[0].Thumbnail, "Steam clan image from BBCode body");
                Eq("https://cdn.example.com/news.png", rich[1].Thumbnail, "image from HTML body");
                Eq("https://i.ytimg.com/vi/abc123XYZ/hqdefault.jpg", rich[2].Thumbnail, "preview video thumbnail");
                Eq(null, rich[3].Thumbnail, "webp is left out so the caller can use game art");
                Yes(SteamNewsClient.Url("57 0", 2).Contains("57%200"), "app id is escaped");
            });

            Group("ValidUrl", delegate
            {
                Yes(SteamNewsClient.ValidUrl("https://store.steampowered.com/news/app/570/view/1"), "https accepted");
                Yes(SteamNewsClient.ValidUrl("http://example.com/a"), "http accepted");
                Yes(!SteamNewsClient.ValidUrl("file:///C:/Windows/System32/calc.exe"), "file: rejected");
                Yes(!SteamNewsClient.ValidUrl("/news/local"), "relative URL rejected");
                Yes(!SteamNewsClient.ValidUrl("https://user:pass@example.com/"), "UserInfo rejected");
                Yes(!SteamNewsClient.ValidUrl("javascript:alert(1)"), "javascript scheme rejected");
                Yes(!SteamNewsClient.ValidUrl("https://example.com/\r\nHeader: x"), "control characters rejected");
                Yes(!SteamNewsClient.ValidUrl(""), "empty rejected");
                Yes(!SteamNewsClient.ValidUrl("http://localhost/x.png"), "localhost rejected");
                Yes(!SteamNewsClient.ValidUrl("http://127.0.0.1/x.png"), "loopback rejected");
                Yes(!SteamNewsClient.ValidUrl("http://192.168.1.10/x.png"), "home network rejected");
                Yes(!SteamNewsClient.ValidUrl("http://10.0.0.5/x.png"), "10.x rejected");
                Yes(!SteamNewsClient.ValidUrl("http://172.20.0.1/x.png"), "172.16/12 rejected");
                Yes(!SteamNewsClient.ValidUrl("http://[::1]/x.png"), "IPv6 loopback rejected");
                Yes(SteamNewsClient.ValidUrl("https://clan.cloudflare.steamstatic.com/images/1/a.jpg"), "a public CDN host is accepted");
            });

            Group("CacheRoundTrip", delegate
            {
                var ser = new JavaScriptSerializer();
                var original = SteamNewsClient.Parse(SteamJson);
                var copy = ser.Deserialize<List<SteamNewsArticle>>(ser.Serialize(original));
                Eq(2, copy.Count, "cached articles survive serialization");
                Eq(original[0].Title, copy[0].Title, "cached title");
                Eq(original[0].Published, copy[0].Published, "cached date");
                Eq(original[0].Url, copy[0].Url, "cached URL");
                var cache = new Dictionary<string, TestGameNewsCacheEntry>();
                cache["570"] = new TestGameNewsCacheEntry { SavedTicks = DateTime.UtcNow.Ticks, Articles = original };
                var disk = ser.Deserialize<Dictionary<string, TestGameNewsCacheEntry>>(ser.Serialize(cache));
                Eq(2, disk["570"].Articles.Count, "app-id keyed disk cache entries survive serialization");
                Yes(disk["570"].SavedTicks > 0, "cache timestamp survives serialization");
            });

            Console.WriteLine(failures == 0 ? "All game news checks passed." : (failures + " game news check(s) failed."));
            return failures == 0 ? 0 : 1;
        }
    }
}
