// Loopback HTTP server: player page, DASH manifest, token-based googlevideo proxy (Range pass-through), VTT captions.
// C# 5 only. Binds 127.0.0.1 on a random free port, started lazily.
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Xml;

namespace SeleneBrowser
{
    public static class LocalServer
    {
        // Kind: "" = plain stream/caption token, "pl" = HLS playlist (rewritten on the way through), "seg" = HLS media segment/key
        class TokenEntry { public string Url; public bool IsCaption; public string Vtt; public DateTime Expires; public string Kind = ""; public string Ua; public string VideoId; public int Itag; }
        class CacheEntry { public StreamInfo Info; public DateTime At; public List<string> Tokens = new List<string>(); }
        class Req
        {
            public string Method, Path, Query, Version;
            public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public NameValueCollection Q;
        }

        static readonly object lk = new object();
        static TcpListener listener;
        static Thread acceptThread;
        static int port;
        static readonly Dictionary<string, TokenEntry> tokens = new Dictionary<string, TokenEntry>();
        static readonly Dictionary<string, CacheEntry> cache = new Dictionary<string, CacheEntry>();
        static readonly Regex IdRx = new Regex("^[A-Za-z0-9_-]{6,20}$", RegexOptions.Compiled);
        static readonly RandomNumberGenerator rng = RandomNumberGenerator.Create();
        static readonly Dictionary<string, string> hlsMap = new Dictionary<string, string>();   // kind|upstream url -> token (stable across playlist reloads)
        static readonly Regex UriAttr = new Regex("URI=\"([^\"]*)\"", RegexOptions.Compiled);

        // ---------------------------------------------------------------- public API
        public static int Port { get { Start(); return port; } }
        public static string BaseUrl { get { Start(); return "http://127.0.0.1:" + port; } }
        public static bool IsRunning { get { return listener != null; } }

        public static bool Start()
        {
            lock (lk)
            {
                if (listener != null) return true;
                try
                {
                    Http.Init();
                    TcpListener l = new TcpListener(IPAddress.Loopback, 0);
                    l.Start(64);
                    port = ((IPEndPoint)l.LocalEndpoint).Port;
                    listener = l;
                    acceptThread = new Thread(AcceptLoop);
                    acceptThread.IsBackground = true;
                    acceptThread.Name = "NomadLocalServer";
                    acceptThread.Start();
                    return true;
                }
                catch { listener = null; return false; }
            }
        }

        public static void Stop()
        {
            lock (lk)
            {
                try { if (listener != null) listener.Stop(); } catch { }
                listener = null;
                tokens.Clear();
                cache.Clear();
                hlsMap.Clear();
            }
        }

        /// <summary>Drops the stream/token caches (YouTube closed) but keeps the server running for other users.</summary>
        public static void TrimCaches()
        {
            lock (lk) { tokens.Clear(); cache.Clear(); hlsMap.Clear(); }
            lock (refreshLk) lastRefresh.Clear();
        }

        /// <summary>URL to load in the CEF view. maxHeight 0 = no cap. Streams are fetched lazily when the page loads.</summary>
        public static string PlayerUrl(string videoId, double startSeconds, int maxHeight, bool sponsorBlock)
        {
            return BaseUrl + "/player?v=" + Uri.EscapeDataString(videoId)
                + "&t=" + ((int)Math.Max(0, startSeconds)).ToString(CultureInfo.InvariantCulture)
                + "&q=" + Math.Max(0, maxHeight).ToString(CultureInfo.InvariantCulture)
                + (sponsorBlock ? "&sb=1" : "");
        }

        /// <summary>Pre-fetch streams (so the page opens instantly and the UI can show an error before navigating).</summary>
        public static Task<Result<StreamInfo>> Prepare(string videoId)
        {
            return Task.Run(() => GetCached(videoId, false));
        }

        // ---------------------------------------------------------------- stream cache / tokens
        static string NewToken()
        {
            byte[] b = new byte[12];
            rng.GetBytes(b);
            StringBuilder sb = new StringBuilder();
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        static string AddToken(string url, bool caption, CacheEntry ce)
        {
            string t = NewToken();
            lock (lk)
            {
                TokenEntry e = new TokenEntry();
                e.Url = url; e.IsCaption = caption; e.Expires = DateTime.UtcNow.AddHours(3);
                tokens[t] = e;
                if (ce != null) ce.Tokens.Add(t);
            }
            return t;
        }

        // Token for one live segment format (served as /lseg/<token>?sq=N). Remembers video + itag so the url can be refreshed.
        static string AddLiveToken(StreamFormat f, string videoId, CacheEntry ce)
        {
            string t = AddToken(f.Url, false, ce);
            lock (lk)
            {
                TokenEntry e = tokens[t];
                e.Kind = "lseg"; e.Ua = Http.IosUA; e.VideoId = videoId; e.Itag = f.Itag;
            }
            return t;
        }

        static readonly object refreshLk = new object();
        static readonly Dictionary<string, DateTime> lastRefresh = new Dictionary<string, DateTime>();

        // format urls expire after hours: re-fetch the IOS player response once and swap the urls by itag.
        // Returns true when the token has a different url than staleUrl afterwards (retry is worthwhile).
        static bool RefreshLive(string videoId, string token, string staleUrl)
        {
            lock (refreshLk)
            {
                TokenEntry te;
                lock (lk) tokens.TryGetValue(token, out te);
                if (te == null) return false;
                if (te.Url != staleUrl) return true;   // another request already refreshed it
                DateTime last;
                if (lastRefresh.TryGetValue(videoId, out last) && (DateTime.UtcNow - last).TotalSeconds < 15) return false;
                lastRefresh[videoId] = DateTime.UtcNow;
                Dictionary<int, string> fresh = YouTubeApi.RefreshLiveUrls(videoId);
                if (fresh == null) return false;
                lock (lk)
                {
                    CacheEntry ce;
                    if (!cache.TryGetValue(videoId, out ce)) return false;
                    foreach (string t in ce.Tokens)
                    {
                        TokenEntry e;
                        string nu;
                        if (tokens.TryGetValue(t, out e) && e.Kind == "lseg" && fresh.TryGetValue(e.Itag, out nu)) e.Url = nu;
                    }
                    foreach (StreamFormat f in ce.Info.LiveVideo) { string nu2; if (fresh.TryGetValue(f.Itag, out nu2)) f.Url = nu2; }
                    string nu3;
                    if (ce.Info.LiveAudio != null && fresh.TryGetValue(ce.Info.LiveAudio.Itag, out nu3)) ce.Info.LiveAudio.Url = nu3;
                }
                return te.Url != staleUrl;
            }
        }

        // Token for one upstream HLS URL. The same URL always maps to the same token, so reloading a live playlist
        // every few seconds does not grow the table. Never put raw URLs in the page.
        static string HlsToken(string url, string kind)
        {
            string key = kind + "|" + url;
            lock (lk)
            {
                string t;
                if (hlsMap.TryGetValue(key, out t)) return t;
                if (hlsMap.Count > 30000)
                {
                    foreach (string old in hlsMap.Values) tokens.Remove(old);
                    hlsMap.Clear();
                }
                t = NewToken();
                TokenEntry e = new TokenEntry();
                e.Url = url; e.Kind = kind; e.Ua = Http.WebUA; e.Expires = DateTime.UtcNow.AddHours(12);
                tokens[t] = e;
                hlsMap[key] = t;
                return t;
            }
        }

        static Result<StreamInfo> GetCached(string videoId, bool force)
        {
            if (!IdRx.IsMatch(videoId ?? "")) return Result<StreamInfo>.Fail("Invalid video id.");
            lock (lk)
            {
                CacheEntry ce;
                // a forced refresh is throttled: several retries within 20 s share one fetch
                // (and never once its stream urls are about to expire: those only answer 403)
                if (cache.TryGetValue(videoId, out ce) && (force ? (DateTime.UtcNow - ce.At).TotalSeconds < 20 : (DateTime.UtcNow - ce.At).TotalMinutes < (ce.Info.IsLive ? 10 : 90))
                    && (ce.Info.IsLive || !YouTubeApi.UrlsExpireSoon(ce.Info, YouTubeApi.RefreshStreamSeconds)))
                    return Result<StreamInfo>.Success(ce.Info);
            }
            Result<StreamInfo> r = YouTubeApi.GetStreamsSync(videoId);
            if (!r.Ok) return r;
            Register(r.Value);
            return r;
        }

        static void Register(StreamInfo si)
        {
            CacheEntry ce = new CacheEntry();
            ce.Info = si; ce.At = DateTime.UtcNow;
            foreach (StreamFormat f in si.Video) f.Token = AddToken(f.Url, false, ce);
            foreach (StreamFormat f in si.Audio) f.Token = AddToken(f.Url, false, ce);
            foreach (StreamFormat f in si.Muxed) f.Token = AddToken(f.Url, false, ce);
            foreach (StreamFormat f in si.LiveVideo) f.Token = AddLiveToken(f, si.VideoId, ce);
            if (si.LiveAudio != null) si.LiveAudio.Token = AddLiveToken(si.LiveAudio, si.VideoId, ce);
            foreach (CaptionTrack c in si.Captions) c.Token = AddToken(c.Url, true, ce);
            if (si.IsLive && si.HlsUrl.Length > 0) si.HlsToken = HlsToken(si.HlsUrl, "pl");
            lock (lk)
            {
                CacheEntry old;
                if (cache.TryGetValue(si.VideoId, out old)) foreach (string t in old.Tokens) tokens.Remove(t);
                cache[si.VideoId] = ce;
                if (cache.Count > 12)
                {
                    string oldest = null; DateTime ot = DateTime.MaxValue;
                    foreach (KeyValuePair<string, CacheEntry> kv in cache) if (kv.Value.At < ot) { ot = kv.Value.At; oldest = kv.Key; }
                    if (oldest != null)
                    {
                        foreach (string t in cache[oldest].Tokens) tokens.Remove(t);
                        cache.Remove(oldest);
                    }
                }
            }
        }

        // ---------------------------------------------------------------- DASH manifest
        static bool IsVp9(StreamFormat f) { return f.Codecs.StartsWith("vp09", StringComparison.OrdinalIgnoreCase) || f.Codecs.StartsWith("vp9", StringComparison.OrdinalIgnoreCase); }
        static bool IsAvc(StreamFormat f) { return f.Codecs.StartsWith("avc1", StringComparison.OrdinalIgnoreCase); }
        static bool IsAv1(StreamFormat f) { return f.Codecs.StartsWith("av01", StringComparison.OrdinalIgnoreCase); }
        static bool IsHdr(StreamFormat f)
        {
            if (f.Codecs.StartsWith("vp09.02", StringComparison.OrdinalIgnoreCase) || f.Codecs.Equals("vp9.2", StringComparison.OrdinalIgnoreCase)) return true;
            string[] p = f.Codecs.Split('.');   // av01.<profile>.<level><tier>.<bit depth>...: 10-bit is YouTube's HDR
            return IsAv1(f) && p.Length > 3 && p[3] == "10";
        }

        /// <summary>The codecs string the manifest declares. YouTube often says just "vp9" / "vp9.2": Chromium plays that,
        /// but Edge (WebView2 too) cannot set up its hardware VP9 decoder from it and the video never gets its first frame.
        /// The full form (profile, level 5.1 = up to 4K60, bit depth) is what Edge needs.</summary>
        public static string DashCodec(StreamFormat f)
        {
            if (f.Codecs.Equals("vp9", StringComparison.OrdinalIgnoreCase)) return "vp09.00.51.08";
            if (f.Codecs.Equals("vp9.2", StringComparison.OrdinalIgnoreCase)) return "vp09.02.51.10";
            return f.Codecs;
        }

        /// <summary>The video renditions of one codec: "av1" (mp4), "vp9" (webm) or "avc" (H.264 mp4). AV1 is the first
        /// choice where the engine decodes it in hardware: YouTube's 4K VP9 webm has seek points ~50 s apart, so one
        /// request is ~180 MB and playback stalls on it; AV1 mp4 comes in small even fragments. H.264 is the last resort.</summary>
        public static List<StreamFormat> PlayableVideo(StreamInfo si, string codec = "vp9")
        {
            List<StreamFormat> sdr = new List<StreamFormat>(), all = new List<StreamFormat>();
            foreach (StreamFormat f in si.Video)
            {
                bool mp4 = f.MimeBase.Equals("video/mp4", StringComparison.OrdinalIgnoreCase), webm = f.MimeBase.Equals("video/webm", StringComparison.OrdinalIgnoreCase);
                if (codec == "av1" ? !mp4 || !IsAv1(f) : codec == "avc" ? !mp4 || !IsAvc(f) : !webm || !IsVp9(f)) continue;
                if (f.IndexEnd <= 0 || f.InitEnd <= 0) continue;
                all.Add(f);
                if (!IsHdr(f)) sdr.Add(f);
            }
            return sdr.Count > 0 ? sdr : all;
        }

        public static List<StreamFormat> PlayableAudio(StreamInfo si)
        {
            List<StreamFormat> res = new List<StreamFormat>();
            foreach (StreamFormat f in si.Audio)
            {
                if (!f.MimeBase.Equals("audio/webm", StringComparison.OrdinalIgnoreCase)) continue;
                if (f.Codecs.ToLowerInvariant().IndexOf("opus") < 0) continue;
                if (f.IndexEnd <= 0 || f.InitEnd <= 0) continue;
                res.Add(f);
            }
            return res;
        }

        static string X(string s) { return System.Security.SecurityElement.Escape(s ?? ""); }

        /// <summary>Builds the static DASH MPD (VP9 webm video, or H.264 mp4 video when avc, + Opus webm audio). Formats must already carry tokens.</summary>
        public static string BuildMpd(StreamInfo si, string codec = "vp9")
        {
            List<StreamFormat> vids = PlayableVideo(si, codec);
            List<StreamFormat> auds = PlayableAudio(si);
            double dur = si.Duration;
            StringBuilder sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            sb.Append("<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" profiles=\"urn:mpeg:dash:profile:isoff-on-demand:2011\" type=\"static\" minBufferTime=\"PT2S\" mediaPresentationDuration=\"PT");
            sb.Append(dur.ToString("0.###", CultureInfo.InvariantCulture)).Append("S\">\n<Period id=\"0\" start=\"PT0S\">\n");

            // video
            List<StreamFormat> sortedV = new List<StreamFormat>(vids);
            sortedV.Sort(delegate (StreamFormat a, StreamFormat b) { return a.Height != b.Height ? a.Height.CompareTo(b.Height) : a.Bitrate.CompareTo(b.Bitrate); });
            HashSet<int> itags = new HashSet<int>();
            sb.Append("<AdaptationSet id=\"0\" contentType=\"video\" mimeType=\"").Append(codec == "vp9" ? "video/webm" : "video/mp4").Append("\" startWithSAP=\"1\" subsegmentAlignment=\"true\" subsegmentStartsWithSAP=\"1\">\n");
            foreach (StreamFormat f in sortedV)
            {
                if (!itags.Add(f.Itag)) continue;
                sb.Append("<Representation id=\"").Append(f.Itag).Append("\" bandwidth=\"").Append(f.Bitrate > 0 ? f.Bitrate : 1000000)
                  .Append("\" codecs=\"").Append(X(DashCodec(f))).Append("\" width=\"").Append(f.Width).Append("\" height=\"").Append(f.Height)
                  .Append("\" frameRate=\"").Append(f.Fps > 0 ? f.Fps : 30).Append("\">\n");
                AppendSegmentInfo(sb, f);
                sb.Append("</Representation>\n");
            }
            sb.Append("</AdaptationSet>\n");

            // audio: one AdaptationSet per audio track (language)
            Dictionary<string, List<StreamFormat>> groups = new Dictionary<string, List<StreamFormat>>();
            List<string> order = new List<string>();
            foreach (StreamFormat f in auds)
            {
                string key = f.AudioTrackId.Length > 0 ? f.AudioTrackId : "und";
                if (!groups.ContainsKey(key)) { groups[key] = new List<StreamFormat>(); order.Add(key); }
                groups[key].Add(f);
            }
            bool anyDefault = false;
            foreach (string k in order) foreach (StreamFormat f in groups[k]) if (f.IsDefaultAudio) anyDefault = true;
            int setId = 1;
            bool mainGiven = false;
            foreach (string k in order)
            {
                List<StreamFormat> g = groups[k];
                bool isDef = false;
                foreach (StreamFormat f in g) if (f.IsDefaultAudio) isDef = true;
                if (!anyDefault && !mainGiven) isDef = true;
                string role = (isDef && !mainGiven) ? "main" : "dub";
                if (role == "main") mainGiven = true;
                string label = g[0].AudioTrackName.Length > 0 ? g[0].AudioTrackName : g[0].AudioLang;
                itags.Clear();
                sb.Append("<AdaptationSet id=\"").Append(setId++).Append("\" contentType=\"audio\" mimeType=\"audio/webm\" lang=\"").Append(X(g[0].AudioLang)).Append("\" startWithSAP=\"1\" subsegmentAlignment=\"true\">\n");
                sb.Append("<Role schemeIdUri=\"urn:mpeg:dash:role:2011\" value=\"").Append(role).Append("\"/>\n");
                sb.Append("<Label>").Append(X(label)).Append("</Label>\n");
                foreach (StreamFormat f in g)
                {
                    sb.Append("<Representation id=\"").Append(f.Itag).Append(k == "und" ? "" : "-" + X(k.Replace('.', '_'))).Append("\" bandwidth=\"").Append(f.Bitrate > 0 ? f.Bitrate : 128000)
                      .Append("\" codecs=\"opus\" audioSamplingRate=\"48000\">\n");
                    sb.Append("<AudioChannelConfiguration schemeIdUri=\"urn:mpeg:dash:23003:3:audio_channel_configuration:2011\" value=\"2\"/>\n");
                    AppendSegmentInfo(sb, f);
                    sb.Append("</Representation>\n");
                }
                sb.Append("</AdaptationSet>\n");
            }
            sb.Append("</Period>\n</MPD>\n");
            return sb.ToString();
        }

        static void AppendSegmentInfo(StringBuilder sb, StreamFormat f)
        {
            sb.Append("<BaseURL>/s/").Append(f.Token).Append("</BaseURL>\n");
            sb.Append("<SegmentBase indexRange=\"").Append(f.IndexStart).Append("-").Append(f.IndexEnd).Append("\"><Initialization range=\"")
              .Append(f.InitStart).Append("-").Append(f.InitEnd).Append("\"/></SegmentBase>\n");
        }

        // ---------------------------------------------------------------- accept / connection handling
        static void AcceptLoop()
        {
            TcpListener l = listener;
            while (l != null)
            {
                TcpClient c;
                try { c = l.AcceptTcpClient(); }
                catch { return; }
                Thread t = new Thread(HandleConnection);
                t.IsBackground = true;
                t.Start(c);
            }
        }

        static void HandleConnection(object state)
        {
            TcpClient client = (TcpClient)state;
            try
            {
                client.NoDelay = true;
                NetworkStream ns = client.GetStream();
                ns.ReadTimeout = 90000;
                ns.WriteTimeout = 60000;
                BufferedStream rd = new BufferedStream(ns, 8192);
                while (true)
                {
                    Req req = ReadRequest(rd);
                    if (req == null) break;
                    bool keep;
                    try { keep = Dispatch(ns, req); }
                    catch (IOException) { break; }
                    catch (SocketException) { break; }
                    catch (ObjectDisposedException) { break; }
                    catch (Exception)
                    {
                        try { SendText(ns, 500, "text/plain", "Internal error", false, false); } catch { }
                        break;
                    }
                    if (!keep) break;
                }
            }
            catch { }
            finally { try { client.Close(); } catch { } }
        }

        static Req ReadRequest(Stream s)
        {
            StringBuilder sb = new StringBuilder();
            int prev = 0, matched = 0;
            int guard = 0;
            while (true)
            {
                int b;
                try { b = s.ReadByte(); } catch { return null; }
                if (b < 0) return null;
                sb.Append((char)b);
                if (++guard > 32768) return null;
                // look for \r\n\r\n
                if (b == '\r' && (matched == 0 || matched == 2)) matched++;
                else if (b == '\n' && (matched == 1 || matched == 3)) matched++;
                else matched = (b == '\r') ? 1 : 0;
                prev = b;
                if (matched == 4) break;
            }
            string[] lines = sb.ToString().Split(new string[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return null;
            string[] rl = lines[0].Split(' ');
            if (rl.Length < 3) return null;
            Req r = new Req();
            r.Method = rl[0].ToUpperInvariant();
            string target = rl[1];
            r.Version = rl[2];
            int qi = target.IndexOf('?');
            r.Path = qi >= 0 ? target.Substring(0, qi) : target;
            r.Query = qi >= 0 ? target.Substring(qi + 1) : "";
            r.Q = HttpUtility.ParseQueryString(r.Query);
            for (int i = 1; i < lines.Length; i++)
            {
                int c = lines[i].IndexOf(':');
                if (c > 0) r.Headers[lines[i].Substring(0, c).Trim()] = lines[i].Substring(c + 1).Trim();
            }
            return r;
        }

        static bool WantsKeepAlive(Req r)
        {
            string c;
            if (r.Headers.TryGetValue("Connection", out c))
            {
                if (c.ToLowerInvariant().Contains("close")) return false;
                if (c.ToLowerInvariant().Contains("keep-alive")) return true;
            }
            return r.Version == "HTTP/1.1";
        }

        static bool Dispatch(NetworkStream ns, Req r)
        {
            bool keep = WantsKeepAlive(r);
            bool head = r.Method == "HEAD";
            if (r.Method == "OPTIONS") { SendText(ns, 204, "text/plain", "", head, keep); return keep; }
            if (r.Method != "GET" && r.Method != "HEAD") { SendText(ns, 405, "text/plain", "Method not allowed", head, keep); return keep; }
            string p = r.Path;

            if (p == "/player") return ServePlayer(ns, r, head, keep);
            if (p.StartsWith("/manifest/", StringComparison.Ordinal) && p.EndsWith(".mpd", StringComparison.Ordinal))
            {
                string id = p.Substring(10, p.Length - 14);
                Result<StreamInfo> sr = GetCached(id, r.Q["fresh"] == "1");   // fresh=1: the page's recovery asks for new urls (old ones expire / 403)
                if (!sr.Ok) { SendText(ns, 502, "text/plain", sr.Error, head, keep); return keep; }
                string mpd;
                string mc = r.Q["c"] ?? "";
                try { mpd = BuildMpd(sr.Value, mc == "avc" || mc == "av1" ? mc : "vp9"); }
                catch (Exception e) { SendText(ns, 500, "text/plain", e.Message, head, keep); return keep; }
                SendText(ns, 200, "application/dash+xml", mpd, head, keep);
                return keep;
            }
            if (p.StartsWith("/s/", StringComparison.Ordinal)) return Proxy(ns, r, p.Substring(3), head, keep);
            if (p.StartsWith("/seg/", StringComparison.Ordinal)) return Proxy(ns, r, p.Substring(5), head, keep);
            if (p.StartsWith("/lseg/", StringComparison.Ordinal)) return LiveSegment(ns, r, p.Substring(6), head, keep);
            if (p.StartsWith("/hls/", StringComparison.Ordinal) && p.EndsWith(".m3u8", StringComparison.Ordinal))
                return ServeHlsPlaylist(ns, p.Substring(5, p.Length - 10), head, keep);
            if (p.StartsWith("/cc/", StringComparison.Ordinal) && p.EndsWith(".vtt", StringComparison.Ordinal))
            {
                string t = p.Substring(4, p.Length - 8);
                TokenEntry te;
                lock (lk) tokens.TryGetValue(t, out te);
                if (te == null || !te.IsCaption) { SendText(ns, 404, "text/plain", "Unknown caption", head, keep); return keep; }
                try
                {
                    if (te.Vtt == null) te.Vtt = YouTubeApi.FetchCaptionVtt(te.Url);
                    SendText(ns, 200, "text/vtt; charset=utf-8", te.Vtt, head, keep);
                }
                catch (Exception e) { SendText(ns, 502, "text/plain", Http.Friendly(e), head, keep); }
                return keep;
            }
            if (p == "/api/sponsor")
            {
                string id = r.Q["v"] ?? "";
                List<Dictionary<string, object>> arr = new List<Dictionary<string, object>>();
                if (IdRx.IsMatch(id))
                    foreach (SponsorSegment s in YouTubeApi.SponsorSegmentsSync(id))
                    {
                        Dictionary<string, object> d = new Dictionary<string, object>();
                        d["s"] = s.Start; d["e"] = s.End; d["c"] = s.Category;
                        arr.Add(d);
                    }
                SendText(ns, 200, "application/json", Jx.ToJson(arr), head, keep);
                return keep;
            }
            if (p == "/health") { SendText(ns, 200, "text/plain", "ok", head, keep); return keep; }
            SendText(ns, 404, "text/plain", "Not found", head, keep);
            return keep;
        }

        static bool ServePlayer(NetworkStream ns, Req r, bool head, bool keep)
        {
            string id = r.Q["v"] ?? "";
            int start = 0, q = 0;
            int.TryParse(r.Q["t"] ?? "0", out start);
            int.TryParse(r.Q["q"] ?? "0", out q);
            bool sb = (r.Q["sb"] ?? "") == "1";
            Dictionary<string, object> cfg = new Dictionary<string, object>();
            cfg["id"] = id; cfg["start"] = start; cfg["maxHeight"] = q; cfg["sponsor"] = sb;
            cfg["manifest"] = "/manifest/" + id + ".mpd";
            cfg["error"] = ""; cfg["title"] = ""; cfg["duration"] = 0;
            List<Dictionary<string, object>> caps = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> auds = new List<Dictionary<string, object>>();
            bool live = false, seg = false;
            Result<StreamInfo> sr = GetCached(id, false);
            if (!sr.Ok) cfg["error"] = sr.Error;
            else
            {
                StreamInfo si = sr.Value;
                cfg["title"] = si.Title; cfg["duration"] = si.Duration;
                if (si.IsLive && si.LiveVideo.Count > 0 && si.LiveAudio != null)
                {
                    live = true; seg = true;
                    cfg["live"] = true;
                    cfg["seg"] = SegConfig(si);
                    cfg["sponsor"] = false;
                    cfg["start"] = 0;
                }
                else if (si.IsLive && si.HlsToken.Length > 0)
                {
                    live = true;
                    cfg["live"] = true;
                    cfg["hls"] = "/hls/" + si.HlsToken + ".m3u8";
                    cfg["sponsor"] = false;
                    cfg["start"] = 0;
                }
                else if (si.IsLive) cfg["error"] = "This live stream is not available right now.";
                else if (si.Duration <= 0) cfg["error"] = "Live streams are not supported yet.";
                // codec candidates, best first: the page plays the first one the engine can decode, asked about the best
                // rendition under the cap with its full codec string (Edge says no to a bare "vp9", yes to "vp09.00.51.08")
                List<object> cands = new List<object>();
                foreach (string codec in new[] { "av1", "vp9", "avc" })
                {
                    StreamFormat top = null;
                    foreach (StreamFormat f in PlayableVideo(si, codec))
                        if ((q <= 0 || f.Height <= q) && (top == null || f.Height > top.Height || f.Height == top.Height && f.Fps > top.Fps)) top = f;
                    if (top == null) continue;
                    Dictionary<string, object> c = new Dictionary<string, object>();
                    c["name"] = codec; c["suffix"] = codec == "vp9" ? "" : "?c=" + codec;
                    c["type"] = (codec == "vp9" ? "video/webm" : "video/mp4") + "; codecs=" + DashCodec(top);
                    c["w"] = top.Width; c["h"] = top.Height; c["fps"] = top.Fps > 0 ? top.Fps : 30; c["rate"] = top.Bitrate > 0 ? top.Bitrate : 4000000;
                    cands.Add(c);
                }
                cfg["cands"] = cands;
                if (!live && cfg["error"].ToString().Length == 0 && (cands.Count == 0 || PlayableAudio(si).Count == 0)) cfg["error"] = "No playable streams are available for this video.";
                foreach (CaptionTrack c in si.Captions)
                {
                    Dictionary<string, object> d = new Dictionary<string, object>();
                    d["lang"] = c.Lang; d["label"] = c.Label; d["url"] = "/cc/" + c.Token + ".vtt";
                    caps.Add(d);
                }
                HashSet<string> seen = new HashSet<string>();
                bool anyDef = false;
                foreach (StreamFormat f in PlayableAudio(si)) if (f.IsDefaultAudio) anyDef = true;
                foreach (StreamFormat f in PlayableAudio(si))
                {
                    string key = f.AudioTrackId.Length > 0 ? f.AudioTrackId : "und";
                    if (!seen.Add(key)) continue;
                    Dictionary<string, object> d = new Dictionary<string, object>();
                    d["lang"] = f.AudioLang;
                    d["label"] = f.AudioTrackName.Length > 0 ? f.AudioTrackName : f.AudioLang;
                    d["isDefault"] = anyDef ? f.IsDefaultAudio : auds.Count == 0;
                    auds.Add(d);
                }
            }
            cfg["captions"] = caps; cfg["audios"] = auds;
            SendText(ns, 200, "text/html; charset=utf-8", PlayerPage.Build(Jx.ToJson(cfg), seg ? "seg" : (live ? "hls" : "dash")), head, keep);
            return keep;
        }

        // all SDR VP9 webm itags share one codecs string, so switching between them never needs changeType. The full form,
        // not YouTube's bare "vp9": Edge cannot set up its hardware VP9 decoder from that, and 1440p+ never showed.
        static string SbMime(StreamFormat f)
        {
            if (f.MimeBase.EndsWith("webm", StringComparison.OrdinalIgnoreCase) && IsVp9(f)) return "video/webm; codecs=\"" + (IsHdr(f) ? "vp09.02.51.10" : "vp09.00.51.08") + "\"";
            return f.MimeBase + "; codecs=\"" + f.Codecs + "\"";
        }

        // config of the live-segment (MSE) player: video ladder + audio, tokens only (never upstream urls)
        static Dictionary<string, object> SegConfig(StreamInfo si)
        {
            Dictionary<string, object> c = new Dictionary<string, object>();
            c["seconds"] = si.SegmentSeconds;
            List<Dictionary<string, object>> vids = new List<Dictionary<string, object>>();
            foreach (StreamFormat f in si.LiveVideo)
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["itag"] = f.Itag; d["height"] = f.Height; d["fps"] = f.Fps; d["token"] = f.Token;
                d["mime"] = SbMime(f); d["webm"] = f.MimeBase.EndsWith("webm", StringComparison.OrdinalIgnoreCase);
                vids.Add(d);
            }
            c["video"] = vids;
            Dictionary<string, object> a = new Dictionary<string, object>();
            a["itag"] = si.LiveAudio.Itag; a["token"] = si.LiveAudio.Token; a["mime"] = SbMime(si.LiveAudio);
            c["audio"] = a;
            return c;
        }

        // ---------------------------------------------------------------- live segments: /lseg/<token>[?sq=N]
        static HttpWebResponse OpenUpstream(string url, string ua, int timeoutMs)
        {
            HttpWebRequest req = Http.Create("GET", url, timeoutMs, ua);
            req.AutomaticDecompression = DecompressionMethods.None;
            req.AllowAutoRedirect = true;   // mp4 itags answer 302
            req.KeepAlive = true;
            req.Headers["Accept-Encoding"] = "identity";
            try { return (HttpWebResponse)req.GetResponse(); }
            catch (WebException wex)
            {
                HttpWebResponse er = wex.Response as HttpWebResponse;
                if (er == null) throw;
                return er;   // 403 / 404 etc: the caller looks at the status and headers
            }
        }

        static bool LiveSegment(NetworkStream ns, Req r, string token, bool head, bool keep)
        {
            TokenEntry te;
            lock (lk) tokens.TryGetValue(token, out te);
            if (te == null || te.Kind != "lseg") { SendText(ns, 404, "text/plain", "Unknown stream", head, keep); return keep; }
            long sq = -1;
            string sqs = r.Q["sq"];
            if (!string.IsNullOrEmpty(sqs) && (!long.TryParse(sqs, NumberStyles.None, CultureInfo.InvariantCulture, out sq) || sq < 0))
            { SendText(ns, 400, "text/plain", "Bad sq", head, keep); return keep; }

            HttpWebResponse resp = null;
            try
            {
                string url = te.Url;
                try
                {
                    resp = OpenUpstream(sq >= 0 ? url + "&sq=" + sq.ToString(CultureInfo.InvariantCulture) : url, te.Ua, 25000);
                    if ((int)resp.StatusCode == 403)
                    {
                        resp.Close(); resp = null;
                        if (RefreshLive(te.VideoId, token, url))
                        {
                            url = te.Url;
                            resp = OpenUpstream(sq >= 0 ? url + "&sq=" + sq.ToString(CultureInfo.InvariantCulture) : url, te.Ua, 25000);
                        }
                    }
                }
                catch (WebException wex) { SendText(ns, 502, "text/plain", Http.Friendly(wex), head, keep); return keep; }
                if (resp == null) { SendText(ns, 403, "text/plain", "Live stream url expired", head, keep); return keep; }

                int code = (int)resp.StatusCode;
                long len = resp.ContentLength;
                StringBuilder h = new StringBuilder();
                h.Append("HTTP/1.1 ").Append(code).Append(' ').Append(StatusText(code)).Append("\r\n");
                string ct = resp.ContentType;
                h.Append("Content-Type: ").Append(string.IsNullOrEmpty(ct) ? "application/octet-stream" : ct).Append("\r\n");
                string hs = resp.Headers["X-Head-Seqnum"], ht = resp.Headers["X-Head-Time-Millis"];
                if (!string.IsNullOrEmpty(hs)) h.Append("X-Head-Seqnum: ").Append(hs.Trim()).Append("\r\n");
                if (!string.IsNullOrEmpty(ht)) h.Append("X-Head-Time-Millis: ").Append(ht.Trim()).Append("\r\n");
                h.Append("Access-Control-Allow-Origin: *\r\nAccess-Control-Expose-Headers: X-Head-Seqnum, X-Head-Time-Millis\r\nCache-Control: no-store\r\n");
                bool chunkless = len >= 0;
                if (chunkless) h.Append("Content-Length: ").Append(len).Append("\r\n");
                bool keepThis = keep && chunkless;
                h.Append(keepThis ? "Connection: keep-alive\r\n" : "Connection: close\r\n");
                h.Append("\r\n");
                byte[] hb = Encoding.ASCII.GetBytes(h.ToString());
                ns.Write(hb, 0, hb.Length);
                if (!head)
                {
                    using (Stream rs = resp.GetResponseStream())
                    {
                        byte[] buf = new byte[65536];
                        long left = len;
                        int n;
                        while ((n = rs.Read(buf, 0, buf.Length)) > 0)
                        {
                            ns.Write(buf, 0, n);
                            if (left >= 0) { left -= n; if (left <= 0) break; }
                        }
                        if (left > 0) return false;
                    }
                }
                ns.Flush();
                return keepThis;
            }
            catch (IOException) { return false; }
            catch (SocketException) { return false; }
            catch (WebException) { return false; }
            finally { if (resp != null) { try { resp.Close(); } catch { } } }
        }

        // ---------------------------------------------------------------- HLS (live): playlist rewriting
        static bool IsPlaylistUrl(string u)
        {
            int q = u.IndexOf('?');
            string path = q >= 0 ? u.Substring(0, q) : u;
            return path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) || path.IndexOf("/hls_playlist/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // upstream reference (absolute, or relative to the playlist) -> local /hls/ or /seg/ URL
        static string MapUri(string u, Uri baseUri, bool playlist)
        {
            if (u.Length == 0 || u.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return u;
            string abs;
            if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) abs = u;
            else
            {
                Uri res;
                if (baseUri == null || !Uri.TryCreate(baseUri, u, out res)) return u;
                abs = res.AbsoluteUri;
            }
            if (!playlist && IsPlaylistUrl(abs)) playlist = true;
            return playlist ? "/hls/" + HlsToken(abs, "pl") + ".m3u8" : "/seg/" + HlsToken(abs, "seg");
        }

        static string RewritePlaylist(string text, Uri baseUri)
        {
            StringBuilder sb = new StringBuilder(text.Length + 4096);
            bool nextIsVariant = false;
            foreach (string raw in text.Split('\n'))
            {
                string t = raw.Trim();
                if (t.Length == 0) continue;
                if (t[0] == '#')
                {
                    if (t.StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal)) nextIsVariant = true;
                    int c = t.IndexOf(':');
                    string tag = c > 0 ? t.Substring(0, c) : t;
                    bool pl = tag == "#EXT-X-MEDIA" || tag == "#EXT-X-I-FRAME-STREAM-INF";
                    Uri bu = baseUri;
                    sb.Append(UriAttr.Replace(t, delegate(Match m) { return "URI=\"" + MapUri(m.Groups[1].Value, bu, pl) + "\""; })).Append('\n');
                    continue;
                }
                sb.Append(MapUri(t, baseUri, nextIsVariant)).Append('\n');
                nextIsVariant = false;
            }
            return sb.ToString();
        }

        static bool ServeHlsPlaylist(NetworkStream ns, string token, bool head, bool keep)
        {
            TokenEntry te;
            lock (lk) tokens.TryGetValue(token, out te);
            if (te == null || te.Kind != "pl") { SendText(ns, 404, "text/plain", "Unknown playlist", head, keep); return keep; }
            string text;
            Uri finalUri;
            HttpWebResponse resp = null;
            try
            {
                HttpWebRequest req = Http.Create("GET", te.Url, 20000, te.Ua);
                try { resp = (HttpWebResponse)req.GetResponse(); }
                catch (WebException wex)
                {
                    HttpWebResponse er = wex.Response as HttpWebResponse;
                    string why = er != null ? "Upstream playlist answered HTTP " + (int)er.StatusCode : Http.Friendly(wex);
                    if (er != null) er.Close();
                    SendText(ns, 502, "text/plain", why, head, keep);
                    return keep;
                }
                finalUri = resp.ResponseUri;   // after redirects: relative references resolve against this
                using (Stream s = resp.GetResponseStream())
                using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                    text = sr.ReadToEnd();
            }
            catch (IOException) { SendText(ns, 502, "text/plain", "Playlist download interrupted", head, keep); return keep; }
            finally { if (resp != null) { try { resp.Close(); } catch { } } }
            if (!text.TrimStart().StartsWith("#EXTM3U", StringComparison.Ordinal))
            {
                SendText(ns, 502, "text/plain", "Upstream did not return a playlist", head, keep);
                return keep;
            }
            SendText(ns, 200, "application/vnd.apple.mpegurl", RewritePlaylist(text, finalUri), head, keep);
            return keep;
        }

        // ---------------------------------------------------------------- responses
        static string StatusText(int code)
        {
            switch (code)
            {
                case 200: return "OK"; case 204: return "No Content"; case 206: return "Partial Content"; case 304: return "Not Modified";
                case 400: return "Bad Request"; case 403: return "Forbidden"; case 404: return "Not Found"; case 405: return "Method Not Allowed";
                case 416: return "Range Not Satisfiable"; case 500: return "Internal Server Error"; case 502: return "Bad Gateway";
                default: return "Status";
            }
        }

        static void SendText(Stream ns, int code, string contentType, string body, bool head, bool keep)
        {
            byte[] b = Encoding.UTF8.GetBytes(body ?? "");
            StringBuilder h = new StringBuilder();
            h.Append("HTTP/1.1 ").Append(code).Append(' ').Append(StatusText(code)).Append("\r\n");
            h.Append("Content-Type: ").Append(contentType).Append("\r\n");
            h.Append("Content-Length: ").Append(b.Length).Append("\r\n");
            h.Append("Access-Control-Allow-Origin: *\r\nCache-Control: no-store\r\n");
            h.Append(keep ? "Connection: keep-alive\r\n" : "Connection: close\r\n");
            h.Append("\r\n");
            byte[] hb = Encoding.ASCII.GetBytes(h.ToString());
            ns.Write(hb, 0, hb.Length);
            if (!head && b.Length > 0) ns.Write(b, 0, b.Length);
            ns.Flush();
        }

        static bool Proxy(NetworkStream ns, Req r, string token, bool head, bool keep)
        {
            TokenEntry te;
            lock (lk) tokens.TryGetValue(token, out te);
            if (te == null || te.IsCaption) { SendText(ns, 404, "text/plain", "Unknown stream", head, keep); return keep; }

            HttpWebResponse resp = null;
            try
            {
                HttpWebRequest req = Http.Create(head ? "HEAD" : "GET", te.Url, 25000, te.Ua ?? Http.VrUA);
                req.AutomaticDecompression = DecompressionMethods.None;
                req.AllowAutoRedirect = true;   // googlevideo 302 -> follow server side
                req.KeepAlive = true;
                req.Headers["Accept-Encoding"] = "identity";
                string range;
                if (r.Headers.TryGetValue("Range", out range))
                {
                    Match m = Regex.Match(range, @"^bytes=(\d*)-(\d*)$");
                    if (m.Success && m.Groups[1].Value.Length > 0)
                    {
                        long a = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                        if (m.Groups[2].Value.Length > 0) req.AddRange(a, long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
                        else req.AddRange(a);
                    }
                    else if (m.Success && m.Groups[2].Value.Length > 0)
                        req.AddRange(-long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
                }
                try { resp = (HttpWebResponse)req.GetResponse(); }
                catch (WebException wex)
                {
                    resp = wex.Response as HttpWebResponse;
                    if (resp == null) { SendText(ns, 502, "text/plain", Http.Friendly(wex), head, keep); return keep; }
                }

                int code = (int)resp.StatusCode;
                long len = resp.ContentLength;
                string upType = resp.ContentType;
                if (te.Kind == "seg" && !head && code == 200 && upType != null && upType.IndexOf("mpegurl", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // a playlist that was referenced like a segment: rewrite it too
                    string ptext;
                    using (Stream ps = resp.GetResponseStream())
                    using (StreamReader psr = new StreamReader(ps, Encoding.UTF8))
                        ptext = psr.ReadToEnd();
                    SendText(ns, 200, "application/vnd.apple.mpegurl", RewritePlaylist(ptext, resp.ResponseUri), false, keep);
                    return keep;
                }
                StringBuilder h = new StringBuilder();
                h.Append("HTTP/1.1 ").Append(code).Append(' ').Append(StatusText(code)).Append("\r\n");
                string ct = resp.ContentType;
                h.Append("Content-Type: ").Append(string.IsNullOrEmpty(ct) ? "application/octet-stream" : ct).Append("\r\n");
                string cr = resp.Headers["Content-Range"];
                if (!string.IsNullOrEmpty(cr)) h.Append("Content-Range: ").Append(cr).Append("\r\n");
                h.Append("Accept-Ranges: bytes\r\n");
                h.Append("Access-Control-Allow-Origin: *\r\nCache-Control: no-store\r\n");
                bool chunkless = len >= 0;
                if (chunkless) h.Append("Content-Length: ").Append(len).Append("\r\n");
                bool keepThis = keep && chunkless;
                h.Append(keepThis ? "Connection: keep-alive\r\n" : "Connection: close\r\n");
                h.Append("\r\n");
                byte[] hb = Encoding.ASCII.GetBytes(h.ToString());
                ns.Write(hb, 0, hb.Length);
                if (!head)
                {
                    using (Stream rs = resp.GetResponseStream())
                    {
                        byte[] buf = new byte[65536];
                        long left = len;
                        int n;
                        while ((n = rs.Read(buf, 0, buf.Length)) > 0)
                        {
                            ns.Write(buf, 0, n);
                            if (left >= 0) { left -= n; if (left <= 0) break; }
                        }
                        if (left > 0) return false; // upstream truncated; connection must close
                    }
                }
                ns.Flush();
                return keepThis;
            }
            catch (IOException) { return false; }
            catch (SocketException) { return false; }
            catch (WebException)
            {
                return false;
            }
            finally { if (resp != null) { try { resp.Close(); } catch { } } }
        }
    }
}
