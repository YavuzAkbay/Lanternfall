// Offline checks for the phone remote (Browser\PhoneRemote.cs + PhoneRemotePage.cs, with PhoneNet.cs for the shared server
// parts): what a link or text means, the routes with a fake TV, tokens, lock-out, the saved token and port, and a real round trip
// over a loopback socket. No WPF, WebView2 or Playnite needed (see phoneremote.ps1). C# 5 only.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Web.Script.Serialization;
using SeleneBrowser;

internal sealed class FakeTv : IRemoteHost
{
    public string Played, Opened, Command, Query, Browsed, Web, Ctx = "home";
    public string PtrAction; public int PtrDx, PtrDy;
    public string Fail;   // when set, every action returns this
    public string PlayVideo(string id) { Played = id; return Fail; }
    public string OpenUrl(string url) { Opened = url; return Fail; }
    public string Control(string command) { Command = command; return Fail; }
    public string Browse(string command) { Browsed = command; return Fail; }
    public string WebSearch(string words) { Web = words; return Fail; }
    public string Pointer(string action, int dx, int dy) { PtrAction = action; PtrDx = dx; PtrDy = dy; return Fail; }
    public Dictionary<string, object> Status()
    {
        return new Dictionary<string, object> { { "ctx", Ctx }, { "playing", Played != null }, { "title", "Song" }, { "paused", false }, { "t", 5.0 }, { "d", 100.0 } };
    }
    public List<Dictionary<string, object>> Search(string query, out string error)
    {
        Query = query;
        error = null;
        return new List<Dictionary<string, object>> { new Dictionary<string, object> { { "id", "dQw4w9WgXcQ" }, { "title", "Result" } } };
    }
}

internal static class PhoneRemoteCheck
{
    private static int failures;
    private static readonly IPAddress Phone = IPAddress.Parse("192.168.178.40");

    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) failures++;
    }

    private static Dictionary<string, object> Parse(string json)
    {
        return new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
    }

    private static string Kind(string input, out string value) { return RemoteLink.Classify(input, out value); }

    private static int Main()
    {
        string v;
        // ---- what a link means
        Check(RemoteLink.YouTubeId("https://www.youtube.com/watch?v=dQw4w9WgXcQ") == "dQw4w9WgXcQ", "watch link");
        Check(RemoteLink.YouTubeId("https://youtu.be/dQw4w9WgXcQ?si=abc") == "dQw4w9WgXcQ", "youtu.be link with a share tracker");
        Check(RemoteLink.YouTubeId("https://m.youtube.com/watch?feature=share&v=dQw4w9WgXcQ&t=30s") == "dQw4w9WgXcQ", "mobile link, v not first");
        Check(RemoteLink.YouTubeId("https://www.youtube.com/shorts/dQw4w9WgXcQ") == "dQw4w9WgXcQ", "shorts link");
        Check(RemoteLink.YouTubeId("https://www.youtube.com/live/dQw4w9WgXcQ") == "dQw4w9WgXcQ", "live link");
        Check(RemoteLink.YouTubeId("https://music.youtube.com/watch?v=dQw4w9WgXcQ") == "dQw4w9WgXcQ", "music link");
        Check(RemoteLink.YouTubeId("youtu.be/dQw4w9WgXcQ") == "dQw4w9WgXcQ", "link without a scheme");
        Check(RemoteLink.YouTubeId("https://www.youtube.com/watch?v=short") == null, "a short id is no video");
        Check(RemoteLink.YouTubeId("https://evil.com/watch?v=dQw4w9WgXcQ") == null, "another site is no YouTube link");
        Check(RemoteLink.YouTubeId("https://youtube.com.evil.com/watch?v=dQw4w9WgXcQ") == null, "a look-alike host is no YouTube link");
        Check(RemoteLink.YouTubeId("https://www.youtube.com/@channel") == null, "a channel is not a video");
        Check(Kind("https://youtu.be/dQw4w9WgXcQ", out v) == RemoteLink.Video && v == "dQw4w9WgXcQ", "classify: video");
        Check(Kind("https://news.ycombinator.com/item?id=1", out v) == RemoteLink.Url && v.StartsWith("https://news.ycombinator.com/"), "classify: web link");
        Check(Kind("example.com/page", out v) == RemoteLink.Url && v == "https://example.com/page", "classify: bare site gets https");
        Check(Kind("lofi hip hop radio", out v) == RemoteLink.Search && v == "lofi hip hop radio", "classify: words search");
        Check(Kind("minecraft", out v) == RemoteLink.Search, "classify: one word searches");
        Check(Kind("javascript:alert(1)", out v) == RemoteLink.Search, "javascript: is never opened");
        Check(Kind("file:///C:/Windows/win.ini", out v) == RemoteLink.Search, "file: is never opened");
        Check(Kind("ftp://x.com/a", out v) == RemoteLink.Search, "ftp: is never opened");
        Check(Kind("https://user:pass@example.com/", out v) == RemoteLink.Search, "a link with a password inside is never opened");
        Check(Kind("   ", out v) == RemoteLink.None && Kind(null, out v) == RemoteLink.None, "empty is nothing");
        Check(Kind(new string('a', 2001), out v) == RemoteLink.None, "huge input is nothing");

        // ---- routes with a fake TV
        var tv = new FakeTv();
        string file = Path.Combine(Path.GetTempPath(), "phone-link-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var remote = new PhoneRemote(tv, file);
            string tok = remote.Token;
            Check(tok != null && tok.Length == 24, "a token exists");
            Check(File.Exists(file), "the token is saved");
            Check(new PhoneRemote(new FakeTv(), file).Token == tok, "the token survives a restart");

            Func<string, string, string, PhoneReply> go = delegate(string method, string path, string body) { return remote.Handle(method, path, body, Phone); };
            PhoneReply r = go("GET", "/r/" + tok, null);
            Check(r.Status == 200 && r.Page && r.Body.Contains("TV remote") && r.Csp != null && r.Csp.Contains("img-src"), "the page is served with its own policy");
            Check(go("GET", "/r/" + tok + "/", null).Status == 200, "trailing slash works");
            Check(go("GET", "/r/wrong", null).Status == 404 && go("GET", "/k/" + tok, null).Status == 404 && go("GET", "/", null).Status == 404, "wrong token / wrong prefix / root are 404");
            Check(remote.Handle("GET", "/r/" + tok, null, IPAddress.Parse("203.0.113.9")).Status == 403, "outside clients are refused");
            Check(go("POST", "/r/" + tok, null).Status == 405, "POST to the page is 405");

            r = go("POST", "/r/" + tok + "/send", "{\"text\":\"https://youtu.be/dQw4w9WgXcQ\"}");
            var d = Parse(r.Body);
            Check(r.Status == 200 && (bool)d["ok"] && (string)d["kind"] == "video" && tv.Played == "dQw4w9WgXcQ", "send: a YouTube link plays on the TV");
            r = go("POST", "/r/" + tok + "/send", "{\"text\":\"https://example.org/a\"}");
            d = Parse(r.Body);
            Check((bool)d["ok"] && (string)d["kind"] == "url" && tv.Opened == "https://example.org/a", "send: a web link opens in the TV browser");
            tv.Opened = null;
            r = go("POST", "/r/" + tok + "/send", "{\"text\":\"cat videos\"}");
            d = Parse(r.Body);
            Check((bool)d["ok"] && (string)d["kind"] == "search" && (string)d["q"] == "cat videos" && tv.Opened == null, "send: words come back as a search, nothing is opened");
            r = go("POST", "/r/" + tok + "/send", "{\"text\":\"javascript:alert(1)\"}");
            Check(tv.Opened == null && (string)Parse(r.Body)["kind"] == "search", "send: javascript: is only a search");
            r = go("POST", "/r/" + tok + "/send", "{\"text\":\"\"}");
            Check(!(bool)Parse(r.Body)["ok"], "send: empty says so");
            Check(go("POST", "/r/" + tok + "/send", "not json").Status == 400, "send: bad json is 400");
            Check(go("GET", "/r/" + tok + "/send", null).Status == 405, "send: GET is 405");

            tv.Played = null;
            r = go("POST", "/r/" + tok + "/play", "{\"id\":\"dQw4w9WgXcQ\"}");
            Check((bool)Parse(r.Body)["ok"] && tv.Played == "dQw4w9WgXcQ", "play: a result plays");
            Check(go("POST", "/r/" + tok + "/play", "{\"id\":\"../../etc\"}").Status == 400 && go("POST", "/r/" + tok + "/play", "{\"id\":\"x\"}").Status == 400, "play: a bad id is 400");

            foreach (string c in PhoneRemote.Commands)
            {
                tv.Command = null;
                r = go("POST", "/r/" + tok + "/ctl", "{\"cmd\":\"" + c + "\"}");
                Check(r.Status == 200 && tv.Command == c, "ctl: " + c);
            }
            tv.Command = null;
            Check(go("POST", "/r/" + tok + "/ctl", "{\"cmd\":\"format\"}").Status == 400 && tv.Command == null, "ctl: an unknown command is 400 and never reaches the TV");

            tv.Fail = "A game is running on the TV.";
            r = go("POST", "/r/" + tok + "/send", "{\"text\":\"https://youtu.be/dQw4w9WgXcQ\"}");
            d = Parse(r.Body);
            Check(!(bool)d["ok"] && (string)d["error"] == "A game is running on the TV.", "the TV's refusal reaches the phone");
            tv.Fail = null;

            r = go("GET", "/r/" + tok + "/search?q=lofi%20beats+radio", null);
            d = Parse(r.Body);
            Check((bool)d["ok"] && tv.Query == "lofi beats radio" && ((object[])d["results"]).Length == 1, "search: the words reach the TV side decoded");
            Check(!(bool)Parse(go("GET", "/r/" + tok + "/search", null).Body)["ok"], "search: no words says so");
            Check(!(bool)Parse(go("GET", "/r/" + tok + "/search?q=" + new string('a', 300), null).Body)["ok"], "search: huge words are refused");

            d = Parse(go("GET", "/r/" + tok + "/state", null).Body);
            Check((bool)d["ok"] && (bool)d["playing"] && (string)d["title"] == "Song", "state: what plays");
            Check(remote.PhoneHere, "a phone that just asked counts as here");

            // ---- the page follows the TV: one page, the TV says what is on
            tv.Ctx = "browser";
            d = Parse(go("GET", "/r/" + tok + "/state", null).Body);
            Check((string)d["ctx"] == "browser" && !d.ContainsKey("kb"), "state: the context and no keyboard");
            Check(PhoneRemotePage.Html.Contains("id='kb'") && PhoneRemotePage.Html.Contains("id='br'") && PhoneRemotePage.Html.Contains("id='yt'"), "the one page holds keyboard, browser and YouTube parts");

            foreach (string c in PhoneRemote.BrowseCommands)
            {
                tv.Browsed = null;
                r = go("POST", "/r/" + tok + "/bctl", "{\"cmd\":\"" + c + "\"}");
                Check(r.Status == 200 && tv.Browsed == c, "bctl: " + c);
            }
            tv.Browsed = null;
            Check(go("POST", "/r/" + tok + "/bctl", "{\"cmd\":\"eval\"}").Status == 400 && tv.Browsed == null, "bctl: an unknown command is 400 and never reaches the TV");
            Check(go("GET", "/r/" + tok + "/bctl", null).Status == 405, "bctl: GET is 405");

            r = go("POST", "/r/" + tok + "/send", "{\"text\":\"best pizza near me\",\"mode\":\"web\"}");
            d = Parse(r.Body);
            Check((bool)d["ok"] && (string)d["kind"] == "web" && tv.Web == "best pizza near me", "send: words in web mode are searched on the TV browser");
            tv.Query = null;
            r = go("POST", "/r/" + tok + "/send", "{\"text\":\"best pizza near me\",\"mode\":\"yt\"}");
            Check((string)Parse(r.Body)["kind"] == "search" && tv.Query == null, "send: words in YouTube mode come back as a YouTube search");
            tv.Played = null;
            r = go("POST", "/r/" + tok + "/send", "{\"text\":\"https://youtu.be/dQw4w9WgXcQ\",\"mode\":\"web\"}");
            Check((string)Parse(r.Body)["kind"] == "video" && tv.Played == "dQw4w9WgXcQ", "send: a YouTube link plays even in web mode");

            // ---- the phone's trackpad
            r = go("POST", "/r/" + tok + "/ptr", "{\"a\":\"move\",\"dx\":12,\"dy\":-7}");
            Check(r.Status == 200 && tv.PtrAction == "move" && tv.PtrDx == 12 && tv.PtrDy == -7, "ptr: a move reaches the TV");
            go("POST", "/r/" + tok + "/ptr", "{\"a\":\"scroll\",\"dx\":0,\"dy\":-40}");
            Check(tv.PtrAction == "scroll" && tv.PtrDy == -40, "ptr: a scroll reaches the TV");
            go("POST", "/r/" + tok + "/ptr", "{\"a\":\"click\"}");
            Check(tv.PtrAction == "click" && tv.PtrDx == 0, "ptr: a click needs no numbers");
            go("POST", "/r/" + tok + "/ptr", "{\"a\":\"move\",\"dx\":99999,\"dy\":-99999}");
            Check(tv.PtrDx == 600 && tv.PtrDy == -600, "ptr: a huge jump is limited");
            go("POST", "/r/" + tok + "/ptr", "{\"a\":\"move\",\"dx\":\"x\",\"dy\":1.6}");
            Check(tv.PtrDx == 0 && tv.PtrDy == 2, "ptr: junk numbers are 0, decimals round");
            tv.PtrAction = null;
            Check(go("POST", "/r/" + tok + "/ptr", "{\"a\":\"drag\"}").Status == 400 && tv.PtrAction == null, "ptr: an unknown action is 400 and never reaches the TV");
            Check(go("GET", "/r/" + tok + "/ptr", null).Status == 405, "ptr: GET is 405");
            tv.Fail = "Close the menu on the TV first.";
            Check((string)Parse(go("POST", "/r/" + tok + "/ptr", "{\"a\":\"click\"}").Body)["error"] == "Close the menu on the TV first.", "ptr: the TV's refusal reaches the phone");
            tv.Fail = null;

            // ---- the keyboard on the same link
            Check(go("POST", "/r/" + tok + "/kb/text", "{\"text\":\"x\"}").Status == 410, "kb: nothing open is 410");
            var typed = new List<string>();
            bool done = false, cancelled = false, closed = false;
            RemoteTyping kb = remote.BeginTyping("Search", "abc", false, false, "#112233");
            kb.TextFromPhone += delegate(string t) { typed.Add(t); };
            kb.Done += delegate { done = true; };
            kb.Cancelled += delegate { cancelled = true; };
            kb.Closed += delegate { closed = true; };
            d = Parse(go("GET", "/r/" + tok + "/state", null).Body);
            var kd = (Dictionary<string, object>)d["kb"];
            Check((string)kd["text"] == "abc" && (string)kd["title"] == "Search" && !(bool)kd["multi"] && !(bool)kd["secret"] && (string)kd["accent"] == "#112233", "state: the open keyboard is described");
            r = go("POST", "/r/" + tok + "/kb/text", "{\"text\":\"abcd\"}");
            Check(r.Status == 200 && typed.Count == 1 && typed[0] == "abcd" && kb.Text == "abcd", "kb: what the phone types reaches the TV");
            go("POST", "/r/" + tok + "/kb/text", "{\"text\":\"abcd\"}");
            Check(typed.Count == 1, "kb: the same text twice is one change");
            int v0 = kb.Version;
            kb.SetText("TV typed");
            d = Parse(go("GET", "/r/" + tok + "/state", null).Body);
            Check(kb.Version == v0 + 1 && (string)((Dictionary<string, object>)d["kb"])["text"] == "TV typed", "kb: what the pad types reaches the phone");
            go("POST", "/r/" + tok + "/kb/text", "{\"text\":\"line1\\nline2\"}");
            Check(kb.Text == "line1 line2", "kb: a single-line field never holds a line break");
            go("POST", "/r/" + tok + "/kb/text", "{\"text\":\"" + new string('z', 5000) + "\"}");
            Check(kb.Text.Length == RemoteTyping.MaxText, "kb: the text is capped");
            Check(go("POST", "/r/" + tok + "/kb/text", "{}").Status == 400, "kb: a body without text is 400");
            Check(go("POST", "/r/" + tok + "/kb/done", "").Status == 200 && done && !cancelled, "kb: Done on the phone reaches the TV");
            Check(go("POST", "/r/" + tok + "/kb/cancel", "").Status == 200 && cancelled, "kb: Cancel on the phone reaches the TV");
            kb.Close();
            Check(closed && !((Dictionary<string, object>)Parse(go("GET", "/r/" + tok + "/state", null).Body)).ContainsKey("kb"), "kb: closing on the TV removes the phone's typing box");
            Check(go("POST", "/r/" + tok + "/kb/done", "").Status == 410, "kb: after closing the phone is told it is gone");
            RemoteTyping first = remote.BeginTyping("A", "", false, false, null);
            bool firstClosed = false;
            first.Closed += delegate { firstClosed = true; };
            RemoteTyping second = remote.BeginTyping("B", "", true, true, null);
            Check(firstClosed && (string)((Dictionary<string, object>)Parse(go("GET", "/r/" + tok + "/state", null).Body)["kb"])["title"] == "B", "kb: a new keyboard replaces the old one");
            Check((bool)((Dictionary<string, object>)Parse(go("GET", "/r/" + tok + "/state", null).Body)["kb"])["secret"], "kb: a password field is marked so the phone hides it");
            second.Close();

            // ---- a phone that arrives (or returns) is announced
            int arrivals = 0;
            var quiet = new PhoneRemote(new FakeTv(), null);
            quiet.PhoneConnected += delegate { arrivals++; };
            quiet.Handle("GET", "/r/" + quiet.Token, null, Phone);
            quiet.Handle("GET", "/r/" + quiet.Token + "/state", null, Phone);
            Check(arrivals == 1 && quiet.PhoneHere, "a phone is announced once while it stays");

            // ---- lock-out after wrong guesses
            var guarded = new PhoneRemote(new FakeTv(), null);
            for (int i = 0; i < PhoneRemote.MaxFailures; i++) guarded.Handle("GET", "/r/nope" + i, null, Phone);
            Check(guarded.Handle("GET", "/r/" + guarded.Token, null, Phone).Status == 429, "after many wrong guesses that device waits, even with the right address");
            Check(guarded.Handle("GET", "/r/" + guarded.Token, null, IPAddress.Parse("192.168.178.41")).Status == 200, "another device is not locked out by it");

            // ---- reset
            string old = remote.Token;
            remote.Reset();
            Check(remote.Token != old && go("GET", "/r/" + old, null).Status == 404 && go("GET", "/r/" + remote.Token, null).Status == 200, "reset: the old address stops, the new one works");
            Check(new PhoneRemote(new FakeTv(), file).Token == remote.Token, "reset: the new token is saved");

            // ---- a real round trip over loopback
            var live = new PhoneRemote(new FakeTv(), null);
            var test = typeof(PhoneNet).GetField("TestAddress", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            test.SetValue(null, IPAddress.Loopback);
            string err = live.Start();
            Check(err == null && live.Running && live.Url != null && live.Url.StartsWith("http://127.0.0.1:") && live.Url.EndsWith("/r/" + live.Token), "starts on the chosen address");
            string reply = Raw(live.Url, "GET", "/state", null);
            Check(reply.StartsWith("HTTP/1.1 200") && reply.Contains("\"ok\":true"), "a real request is answered");
            reply = Raw(live.Url, "POST", "/send", "{\"text\":\"https://youtu.be/dQw4w9WgXcQ\"}");
            Check(reply.Contains("\"kind\":\"video\""), "a real POST is answered");
            string bad = Raw(live.Url.Replace(live.Token, "0000"), "GET", "/state", null);
            Check(bad.StartsWith("HTTP/1.1 404"), "a real wrong token is 404");
            // ---- a flood of silent connections is capped; normal requests work again afterwards
            var holders = new List<TcpClient>();
            for (int i = 0; i < PhoneRemote.MaxConnections + 12; i++)
            {
                var hc = new TcpClient();
                hc.Connect(new Uri(live.Url).Host, new Uri(live.Url).Port);
                holders.Add(hc);
            }
            System.Threading.Thread.Sleep(600);
            int cut = 0;
            foreach (TcpClient hc in holders)
            {
                try
                {
                    hc.Client.Blocking = false;
                    int n = hc.Client.Receive(new byte[1], System.Net.Sockets.SocketFlags.Peek);
                    if (n == 0) cut++;
                }
                catch (SocketException ex) { if (ex.SocketErrorCode != SocketError.WouldBlock) cut++; }
            }
            Check(cut >= 10, "connections beyond the cap are cut at once (" + cut + " of " + holders.Count + ")");
            foreach (TcpClient hc in holders) hc.Close();
            System.Threading.Thread.Sleep(800);
            Check(Raw(live.Url, "GET", "/state", null).StartsWith("HTTP/1.1 200"), "after the flood a normal request is answered");
            int port = new Uri(live.Url).Port;
            live.Stop();
            Check(!live.Running && live.Url == null, "stops");
            Check(port > 0, "it had a port");
        }
        finally { try { File.Delete(file); } catch { } }

        Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILED");
        return failures == 0 ? 0 : 1;
    }

    // one request against base (http://ip:port/r/token) + sub; returns the raw reply
    private static string Raw(string baseUrl, string method, string sub, string body)
    {
        Uri u = new Uri(baseUrl);
        using (var c = new TcpClient())
        {
            c.Connect(u.Host, u.Port);
            c.ReceiveTimeout = 5000;
            var s = c.GetStream();
            byte[] b = Encoding.UTF8.GetBytes(body ?? "");
            string head = method + " " + u.AbsolutePath + sub + " HTTP/1.1\r\nHost: x\r\nContent-Length: " + b.Length + "\r\nConnection: close\r\n\r\n";
            byte[] h = Encoding.ASCII.GetBytes(head);
            s.Write(h, 0, h.Length);
            s.Write(b, 0, b.Length);
            var sb = new StringBuilder();
            var buf = new byte[4096];
            int n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0) sb.Append(Encoding.UTF8.GetString(buf, 0, n));
            return sb.ToString();
        }
    }
}
