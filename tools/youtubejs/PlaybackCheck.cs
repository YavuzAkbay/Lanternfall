// Playback check for tools\update-youtubejs.ps1 (C# 5, built against Browser\build\SeleneBrowser.dll, 32-bit like Playnite).
// Each video id on the command line goes through the YouTube web route alone (the hidden WebView2: BotGuard tokens and
// YouTube.js deciphering), then a 64 KB read is made deep inside one video and one audio stream, well past the ~1 MB where
// googlevideo cuts off links without a valid token. A video passes only if both reads answer 206.
// Exit code 0 = every video passed. Needs the network.
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using SeleneBrowser;

internal static class PlaybackCheck
{
    private const string UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private static int Main(string[] args)
    {
        ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;   // TLS 1.2
        if (args.Length == 0) { Console.WriteLine("usage: check.exe <videoId>..."); return 2; }
        int failed = 0;
        foreach (string id in args)
        {
            try
            {
                string vid = id;
                Result<StreamInfo> r = Task.Run(() => YouTubeApi.GetStreamsViaWeb(vid)).Result;
                if (!r.Ok) { Console.WriteLine("  FAIL " + id + ": " + r.Error); failed++; continue; }
                StreamInfo s = r.Value;
                StreamFormat v = Pick(s.Video), a = Pick(s.Audio);
                if (v == null || a == null) { Console.WriteLine("  FAIL " + id + ": no stream with a known length"); failed++; continue; }
                bool vOk, aOk;
                string vr = Probe(v, out vOk), ar = Probe(a, out aOk);
                bool ok = vOk && aOk;
                if (!ok) failed++;
                Console.WriteLine("  " + (ok ? "PASS " : "FAIL ") + id + " \"" + s.Title + "\": " + s.Video.Count + " video / " + s.Audio.Count
                    + " audio formats; " + v.Height + "p video read " + vr + ", audio read " + ar);
            }
            catch (Exception e)
            {
                Exception x = e is AggregateException && e.InnerException != null ? e.InnerException : e;
                Console.WriteLine("  FAIL " + id + ": " + x.Message);
                failed++;
            }
        }
        return failed == 0 ? 0 : 1;
    }

    // the largest stream (the one most likely to be cut off)
    private static StreamFormat Pick(List<StreamFormat> list)
    {
        StreamFormat best = null;
        foreach (StreamFormat f in list) if (f.ContentLength > 0 && (best == null || f.ContentLength > best.ContentLength)) best = f;
        return best;
    }

    private static string Probe(StreamFormat f, out bool ok)
    {
        ok = false;
        long len = f.ContentLength;
        long start = len > 2000000 ? Math.Max(1200000, len * 6 / 10) : Math.Max(0, len - 65536);
        long end = Math.Min(len - 1, start + 65535);
        string where = (start / 1048576.0).ToString("0.0") + " MB";
        try
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(f.Url);
            req.UserAgent = UA;
            req.Timeout = 20000;
            req.AddRange(start, end);
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            {
                ok = (int)resp.StatusCode == 206;
                return (int)resp.StatusCode + " at " + where;
            }
        }
        catch (WebException e)
        {
            HttpWebResponse resp = e.Response as HttpWebResponse;
            return (resp != null ? ((int)resp.StatusCode).ToString() : e.Status.ToString()) + " at " + where;
        }
    }
}
