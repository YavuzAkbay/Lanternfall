// Regression check for Invidious stream-url expiry handling (finding H2).
// A cached Invidious body with expired googlevideo URLs gets accepted, proxied as 403 on every segment, and the
// player retries forever. The accept gate must reject such a body (without letting unrelated URLs veto a good
// response) and the local cache must never serve URLs that are about to expire.
// C# 5 only. Compiled against the real, built SeleneBrowser.dll - the production code is not copied here.
using System;
using System.Globalization;
using System.Reflection;
using SeleneBrowser;

namespace SeleneBrowser.ExpiryCheck
{
    public static class Check
    {
        static int assertions;

        static void True(bool value, string why)
        {
            if (!value) throw new Exception(why);
            assertions++;
        }

        static void False(bool value, string why)
        {
            if (value) throw new Exception(why);
            assertions++;
        }

        static void Equal(long expected, long actual, string why)
        {
            if (expected != actual)
                throw new Exception(why + ": expected <" + expected + ">, got <" + actual + ">");
            assertions++;
        }

        static long Now()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        static string Url(long expire)
        {
            return "https://r1.example/videoplayback?itag=137&expire="
                + expire.ToString(CultureInfo.InvariantCulture) + "&sig=abc";
        }

        static string Body(string streamUrl, string hlsUrl, string noise)
        {
            return "{\"title\":\"t\",\"liveNow\":false,\"hlsUrl\":\"" + hlsUrl
                + "\",\"adaptiveFormats\":[{\"itag\":137,\"url\":\"" + streamUrl + "\"}]" + noise + "}";
        }

        static string LiveBody(bool withHls, long expire)
        {
            return "{\"title\":\"t\",\"liveNow\":true,\"hlsUrl\":\"" + (withHls ? Url(expire) : "")
                + "\",\"adaptiveFormats\":[{\"itag\":137,\"url\":\"" + Url(expire) + "\"}]}";
        }

        static StreamInfo Info(string url)
        {
            StreamInfo si = new StreamInfo();
            si.VideoId = "abcdefghijk";
            StreamFormat f = new StreamFormat();
            f.Itag = 137; f.Url = url;
            si.Video.Add(f);
            return si;
        }

        static MethodInfo Method(string name)
        {
            MethodInfo m = typeof(YouTubeApi).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
            if (m == null) throw new Exception("YouTubeApi." + name + " is missing - the expiry guard was removed");
            return m;
        }

        static bool Accept(string body)
        {
            return (bool)Method("AcceptStreamBody").Invoke(null, new object[] { body });
        }

        static long ExpiresAt(string url)
        {
            return (long)Method("ExpiresAt").Invoke(null, new object[] { url });
        }

        static int Const(string name)
        {
            FieldInfo f = typeof(YouTubeApi).GetField(name,
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (f == null || !f.IsLiteral)
                throw new Exception("YouTubeApi." + name + " constant is missing");
            return Convert.ToInt32(f.GetRawConstantValue(), CultureInfo.InvariantCulture);
        }

        public static int Main()
        {
            long now = Now();
            long stale = now - 120;
            long fresh = now + 3600;
            int acceptMargin = Const("FreshStreamSeconds");
            int refreshMargin = Const("RefreshStreamSeconds");

            // The stamp reader: raw, escaped and path-style forms, plus the "no stamp" sentinel.
            Equal(stale, ExpiresAt(Url(stale)), "a query-string expire stamp is read");
            Equal(long.MaxValue, ExpiresAt("https://r1.example/videoplayback?itag=137"), "no stamp means MaxValue");
            Equal(stale, ExpiresAt("https://r1.example/videoplayback?a=1\\u0026expire=" + stale),
                "a JSON-escaped ampersand before expire is read");
            Equal(stale, ExpiresAt("https://r1.example/videoplayback/expire/" + stale), "a path-style stamp is read");

            // The accept gate: cached/expired stream urls must count as a failed instance.
            False(Accept(Body(Url(stale), "", "")), "an already-expired stream url rejects the body");
            True(Accept(Body(Url(fresh), "", "")), "a fresh stream url is accepted");
            True(Accept(Body("https://r1.example/videoplayback?itag=137", "", "")),
                "a stream url with no expire stamp is accepted");
            False(Accept(Body(Url(fresh), Url(stale), "")), "a stale hls url rejects the body");
            False(Accept(Body(Url(now + acceptMargin - 100), "", "")),
                "stream urls inside the accept margin are rejected");
            True(Accept(Body(Url(now + acceptMargin + 100), "", "")),
                "stream urls beyond the accept margin are accepted");

            // Hardening: only the urls the player uses may veto a response.
            string noise = ",\"storyboards\":[{\"url\":\"" + Url(stale) + "\"}],"
                + "\"captions\":[{\"language_code\":\"en\",\"url\":\"" + Url(stale) + "\"}],"
                + "\"recommendedVideos\":[{\"videoId\":\"abcdefghijk\",\"title\":\"x\"}]";
            True(Accept(Body(Url(fresh), "", noise)),
                "stale storyboard/caption/recommendation urls do not veto fresh streams");
            True(Accept("not json"), "an unparsable body is left to the normal parse path");

            // The original live/HLS rule must survive the hardening.
            False(Accept(LiveBody(false, fresh)), "a live stream without its HLS manifest is still rejected");
            True(Accept(LiveBody(true, fresh)), "a live stream with HLS and fresh urls is accepted");

            // The local cache guard: don't hand out urls that only have a few minutes left.
            True(acceptMargin > refreshMargin, "the accept margin leaves room for the cache refresh margin");
            True(YouTubeApi.UrlsExpireSoon(Info(Url(stale)), refreshMargin), "already-expired cached urls force a refetch");
            True(YouTubeApi.UrlsExpireSoon(Info(Url(now + 100)), refreshMargin), "cached urls inside the margin force a refetch");
            False(YouTubeApi.UrlsExpireSoon(Info(Url(now + 3600)), refreshMargin), "long-lived cached urls are reused");
            False(YouTubeApi.UrlsExpireSoon(Info("https://r1.example/videoplayback?itag=137"), refreshMargin),
                "cached urls with no stamp are reused");
            False(YouTubeApi.UrlsExpireSoon(null, refreshMargin), "a missing StreamInfo is not treated as expiring");

            Console.WriteLine("browser expiry checks passed (" + assertions + " assertions)");
            return 0;
        }
    }
}
