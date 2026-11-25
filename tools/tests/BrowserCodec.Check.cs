// Regression check for the DASH / MSE codec strings handed to Edge WebView2 (finding H1).
// YouTube often reports bare "vp9" / "vp9.2"; WebView2 cannot set up its VP9 decoder from the alias
// and the first frame never appears. The shipping plugin must always emit the full "vp09..." form.
// C# 5 only. Compiled against the real, built SeleneBrowser.dll - the production code is not copied here.
using System;
using System.Reflection;
using SeleneBrowser;

namespace SeleneBrowser.CodecCheck
{
    public static class Check
    {
        static int assertions;

        static void Equal(string expected, string actual, string reason)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                throw new Exception(reason + ": expected <" + expected + ">, got <" + actual + ">");
            assertions++;
        }

        static void Contains(string needle, string haystack, string reason)
        {
            if (haystack == null || haystack.IndexOf(needle, StringComparison.Ordinal) < 0)
                throw new Exception(reason + ": expected to find <" + needle + ">");
            assertions++;
        }

        static void Absent(string needle, string haystack, string reason)
        {
            if (haystack != null && haystack.IndexOf(needle, StringComparison.Ordinal) >= 0)
                throw new Exception(reason +": did not expect <" + needle + ">");
            assertions++;
        }

        static StreamFormat Video(string codec)
        {
            StreamFormat f = new StreamFormat();
            f.Mime = "video/webm; codecs=\"" + codec + "\"";
            f.MimeBase = "video/webm";
            f.Codecs = codec;
            f.Itag = 244; f.Width = 1920; f.Height = 1080; f.Fps = 30; f.Bitrate = 5000000;
            f.InitStart = 0; f.InitEnd = 100; f.IndexStart = 101; f.IndexEnd = 200;
            f.Token = "test-token";
            return f;
        }

        static StreamFormat Audio()
        {
            StreamFormat f = new StreamFormat();
            f.Mime = "audio/webm; codecs=\"opus\"";
            f.MimeBase = "audio/webm";
            f.Codecs = "opus";
            f.Itag = 251; f.Bitrate = 128000;
            f.InitStart = 0; f.InitEnd = 100; f.IndexStart = 101; f.IndexEnd = 200;
            f.Token = "test-token";
            return f;
        }

        static StreamInfo Info(string videoCodec)
        {
            StreamInfo si = new StreamInfo();
            si.VideoId = "abcdefghijk"; si.Duration = 60;
            si.Video.Add(Video(videoCodec));
            si.Audio.Add(Audio());
            return si;
        }

        static string SbMime(StreamFormat f)
        {
            MethodInfo m = typeof(LocalServer).GetMethod("SbMime", BindingFlags.Static | BindingFlags.NonPublic);
            if (m == null) throw new Exception("LocalServer.SbMime is missing - the MSE path lost its codec guard");
            return (string)m.Invoke(null, new object[] { f });
        }

        public static int Main()
        {
            // The shared normalizer: bare aliases become full codec strings, real strings pass through unchanged.
            Equal("vp09.00.51.08", LocalServer.DashCodec(Video("vp9")), "bare VP9 alias is expanded");
            Equal("vp09.00.51.08", LocalServer.DashCodec(Video("VP9")), "alias expansion ignores case");
            Equal("vp09.02.51.10", LocalServer.DashCodec(Video("vp9.2")), "bare VP9 HDR alias is expanded");
            Equal("vp09.02.51.10", LocalServer.DashCodec(Video("VP9.2")), "HDR alias expansion ignores case");
            Equal("vp09.00.40.08", LocalServer.DashCodec(Video("vp09.00.40.08")), "an already full codec string is not rewritten");
            Equal("avc1.640028", LocalServer.DashCodec(Video("avc1.640028")), "non-VP9 codec strings are left alone");

            // The DASH manifest: no bare alias may reach shaka / WebView2, SDR or HDR.
            string sdr = LocalServer.BuildMpd(Info("vp9"));
            Contains("codecs=\"vp09.00.51.08\"", sdr, "SDR manifest declares the full VP9 codec string");
            Absent("codecs=\"vp9\"", sdr, "SDR manifest never declares the bare alias");
            string hdr = LocalServer.BuildMpd(Info("vp9.2"));
            Contains("codecs=\"vp09.02.51.10\"", hdr, "HDR manifest declares the full VP9 codec string");
            Absent("codecs=\"vp9.2\"", hdr, "HDR manifest never declares the bare alias");

            // The MSE live-segment path uses its own MIME builder; lock that one separately.
            Equal("video/webm; codecs=\"vp09.00.51.08\"", SbMime(Video("vp9")), "SDR MSE MIME uses the full codec string");
            Equal("video/webm; codecs=\"vp09.02.51.10\"", SbMime(Video("vp9.2")), "HDR MSE MIME uses the full codec string");

            Console.WriteLine("browser codec checks passed (" + assertions + " assertions)");
            return 0;
        }
    }
}
