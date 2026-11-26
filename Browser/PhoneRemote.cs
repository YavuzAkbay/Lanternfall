// PhoneRemote.cs - the phone link: ONE page, ONE QR code, for everything. The phone page follows what is on the TV: the keyboard when
// a keyboard is open (it types into the TV's field, both ways), YouTube controls and search while YouTube is up, browser controls
// in the browser, and a plain "send a link" box anywhere. Sending a YouTube link plays it, any other link opens in the TV browser.
// The link stays up while Playnite runs, so the phone can reconnect later with the same address; "Reset phone remote link" in the
// Browser's Settings changes the token and cuts every phone off.
//
// Safety: it exists only after the user switched it on at the TV, is bound to ONE private LAN address, answers only private-range
// clients, every path carries a random 96-bit token (kept in the plugin's data folder), and repeated wrong guesses lock it for
// a few minutes. Plain HTTP on the home network: a password typed on the phone crosses the Wi-Fi unencrypted, so the keyboard
// asks for passwords on the TV unless the user allowed phone passwords in the Browser settings. It refuses to start anything
// while a game runs. This file has no WPF or Playnite references on purpose, so tools\tests\PhoneRemote.Check.cs can compile it
// alone. C# 5 only.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace SeleneBrowser
{
    /// <summary>What a link or a line of text from the phone means.</summary>
    internal static class RemoteLink
    {
        public const string Video = "video", Url = "url", Search = "search", None = "none";
        private static readonly Regex VideoId = new Regex("^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);

        /// <summary>The 11-character YouTube id inside a link (watch, youtu.be, shorts, live, embed), or null.</summary>
        public static string YouTubeId(string link)
        {
            Uri u;
            if (!TryUrl(link, out u)) return null;
            string host = u.Host.ToLowerInvariant();
            if (host.StartsWith("www.", StringComparison.Ordinal)) host = host.Substring(4);
            if (host.StartsWith("m.", StringComparison.Ordinal)) host = host.Substring(2);
            if (host.StartsWith("music.", StringComparison.Ordinal)) host = host.Substring(6);
            string id = null;
            if (host == "youtu.be")
            {
                string[] p = u.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length > 0) id = p[0];
            }
            else if (host == "youtube.com" || host == "youtube-nocookie.com")
            {
                string[] p = u.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length >= 2 && (p[0] == "shorts" || p[0] == "live" || p[0] == "embed" || p[0] == "v")) id = p[1];
                else if (p.Length >= 1 && p[0] == "watch") id = QueryValue(u.Query, "v");
            }
            return id != null && VideoId.IsMatch(id) ? id : null;
        }

        /// <summary>Classifies what the phone sent. value: the video id, the address to open (http / https only), or the search words.</summary>
        public static string Classify(string input, out string value)
        {
            value = "";
            string s = (input ?? "").Trim();
            if (s.Length == 0 || s.Length > 2000) return None;
            string id = YouTubeId(s);
            if (id != null) { value = id; return Video; }
            if (s.IndexOf(' ') < 0 && s.IndexOf('\n') < 0)
            {
                Uri u;
                if (TryUrl(s, out u)) { value = u.AbsoluteUri; return Url; }
            }
            value = s;
            return Search;
        }

        // an address with a scheme, or a bare "site.com/page" (a dot, no spaces); only http(s), no user:password@
        private static bool TryUrl(string s, out Uri u)
        {
            u = null;
            if (string.IsNullOrEmpty(s)) return false;
            string t = s.Trim();
            bool scheme = t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || t.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            if (!scheme)
            {
                if (t.IndexOf(' ') >= 0 || t.IndexOf('.') <= 0 || t.IndexOf("://", StringComparison.Ordinal) >= 0) return false;
                t = "https://" + t;
            }
            if (!Uri.TryCreate(t, UriKind.Absolute, out u)) return false;
            if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return false;
            if (!string.IsNullOrEmpty(u.UserInfo) || string.IsNullOrEmpty(u.Host)) return false;
            return true;
        }

        private static string QueryValue(string query, string key)
        {
            if (string.IsNullOrEmpty(query)) return null;
            foreach (string part in query.TrimStart('?').Split('&'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                if (part.Substring(0, eq) == key) return Uri.UnescapeDataString(part.Substring(eq + 1));
            }
            return null;
        }
    }

    /// <summary>What the TV side does for the phone. Called on server worker threads; implementations hop to the UI thread.
    /// A method returns null when it worked, otherwise a sentence the phone shows.</summary>
    internal interface IRemoteHost
    {
        string PlayVideo(string id);
        string OpenUrl(string url);
        string WebSearch(string words);   // the TV browser searches the web for these words
        string Control(string command);   // YouTube: toggle, back, forward, next, previous, stop
        string Browse(string command);    // browser: back, forward, reload, home, up, down, zoomin, zoomout, newtab, closetab, address
        string Pointer(string action, int dx, int dy);   // browser cursor: move (dx, dy), scroll (dy), click
        /// <summary>ctx = "youtube" | "browser" | "home" (what is on the TV), plus that screen's facts (YouTube: playing, title, author,
        /// paused, t, d, live; browser: tab, url, canBack, canForward).</summary>
        Dictionary<string, object> Status();
        List<Dictionary<string, object>> Search(string query, out string error);   // id, title, author, duration, thumb
        string Pad(string button);        // one controller press anywhere in the theme (PhoneRemote.PadButtons)
        List<Dictionary<string, object>> Games(out string error);   // the library: id, name, inst, fav, run, play, last, cover, src
        string Launch(string id);         // play that game on the TV (a Guid string)
        byte[] Cover(string id);          // a small JPEG of that game's cover, or null
    }

    /// <summary>The keyboard on the TV, as the phone sees it: one at a time. Created by PhoneRemote.BeginTyping; events arrive on
    /// worker threads.</summary>
    internal sealed class RemoteTyping
    {
        public const int MaxText = 4000;
        private readonly object gate = new object();
        private readonly PhoneRemote owner;
        private string text;
        private int version;
        private bool closed;

        public string Title { get; private set; }
        public string Accent { get; private set; }
        public bool Multiline { get; private set; }
        public bool Secret { get; private set; }
        public int Version { get { lock (gate) return version; } }
        public string Text { get { lock (gate) return text; } }
        public bool IsClosed { get { lock (gate) return closed; } }

        /// <summary>The phone typed: the whole field's text now.</summary>
        public event Action<string> TextFromPhone;
        public event Action Done;
        public event Action Cancelled;
        public event Action Closed;

        internal RemoteTyping(PhoneRemote owner, string title, string initial, bool multiline, bool secret, string accent)
        {
            this.owner = owner;
            Title = title ?? "";
            Multiline = multiline;
            Secret = secret;
            Accent = ValidAccent(accent) ? accent : "#7aa2ff";
            text = Clean(initial ?? "");
        }

        private static bool ValidAccent(string s)
        {
            if (s == null || s.Length != 7 || s[0] != '#') return false;
            for (int i = 1; i < 7; i++) if (!Uri.IsHexDigit(s[i])) return false;
            return true;
        }

        // single-line fields never hold a line break; every field is capped
        private string Clean(string value)
        {
            if (value.Length > MaxText) value = value.Substring(0, MaxText);
            if (!Multiline) value = value.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
            else value = value.Replace("\r\n", "\n").Replace('\r', '\n');
            return value;
        }

        /// <summary>The TV's keyboard changed (or the text came from the phone: then it is already the same).</summary>
        public void SetText(string value)
        {
            lock (gate)
            {
                if (closed) return;
                value = Clean(value ?? "");
                if (value == text) return;
                text = value;
                version++;
            }
        }

        public void Close()
        {
            lock (gate) { if (closed) return; closed = true; }
            owner.Detach(this);
            var h = Closed;
            if (h != null) h();
        }

        // the phone side (called by PhoneRemote)
        internal bool FromPhone(string value)
        {
            Action raise = null;
            lock (gate)
            {
                if (closed) return false;
                value = Clean(value ?? "");
                if (value != text)
                {
                    text = value;
                    version++;
                    string sent = value;
                    raise = delegate { var h = TextFromPhone; if (h != null) h(sent); };
                }
            }
            if (raise != null) raise();
            return true;
        }

        internal bool Finish(bool done)
        {
            lock (gate) { if (closed) return false; }
            if (done) { var h = Done; if (h != null) h(); }
            else { var h = Cancelled; if (h != null) h(); }
            return true;
        }
    }

    /// <summary>The persistent phone link: one listener on the PC's home-network address.</summary>
    internal sealed class PhoneRemote
    {
        public const int MaxBody = 16384;
        public const int MaxFailures = 20;
        public const int LockMilliseconds = 5 * 60 * 1000;
        public const int MaxConnections = 24;   // at once: a device that connects and says nothing, or trickles bytes, holds a worker for up to about 10 s (PhoneNet.RequestDeadlineMs)
        public static readonly string[] PointerActions = { "move", "scroll", "click" };
        public static readonly string[] Commands = { "toggle", "back", "forward", "next", "previous", "stop" };
        public static readonly string[] PadButtons = { "A", "B", "X", "Y", "Start", "Back", "LeftShoulder", "RightShoulder", "TriggerLeft", "TriggerRight",
                                                       "DPadUp", "DPadDown", "DPadLeft", "DPadRight", "LeftStick", "RightStick" };
        private static readonly Regex GameId = new Regex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.Compiled);
        public static readonly string[] BrowseCommands = { "back", "forward", "reload", "home", "up", "down", "zoomin", "zoomout", "newtab", "closetab", "address" };

        private readonly object gate = new object();
        private readonly IRemoteHost host;
        private readonly string stateFile;
        private string token;
        private int savedPort;
        private TcpListener listener;
        private sealed class Strikes { public int Count, Start, LockedAt; public bool Locked; }
        private readonly Dictionary<string, Strikes> strikes = new Dictionary<string, Strikes>();   // wrong guesses per device, not for everybody
        private int active;   // connections being served
        private int lastPhoneSeen;
        private bool everSeen;
        private RemoteTyping typing;

        /// <summary>Raised (on a worker thread) when a phone shows up: it loaded the page, or came back after being away.</summary>
        public event Action PhoneConnected;

        public string Url { get; private set; }
        /// <summary>The address it listens on (null when stopped).</summary>
        public IPAddress BoundAddress { get; private set; }
        public bool Running { get { return listener != null; } }
        public string Token { get { return token; } }

        /// <summary>True when a phone fetched the page or its state in the last 8 seconds.</summary>
        public bool PhoneHere
        {
            get { lock (gate) return everSeen && unchecked(Environment.TickCount - lastPhoneSeen) < 8000; }
        }

        /// <param name="stateFile">Where the token and the port are kept between runs (null: not kept, tests).</param>
        public PhoneRemote(IRemoteHost host, string stateFile)
        {
            this.host = host;
            this.stateFile = stateFile;
            Load();
            if (token == null) { token = PhoneNet.NewToken(); Save(); }
        }

        private void Load()
        {
            try
            {
                if (stateFile == null || !File.Exists(stateFile)) return;
                var d = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(stateFile, Encoding.UTF8)) as Dictionary<string, object>;
                if (d == null) return;
                object t, p;
                if (d.TryGetValue("token", out t) && t is string && ((string)t).Length == 24 && Regex.IsMatch((string)t, "^[0-9a-f]+$")) token = (string)t;
                if (d.TryGetValue("port", out p) && p is int && (int)p >= 1024 && (int)p <= 65535) savedPort = (int)p;
            }
            catch { }
        }

        private void Save()
        {
            if (stateFile == null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(stateFile));
                var d = new Dictionary<string, object> { { "token", token }, { "port", savedPort } };
                AtomicFile.WriteAllText(stateFile, new JavaScriptSerializer().Serialize(d), Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>A keyboard opened on the TV: the phone's page shows a typing box for it. Replaces (and closes) an earlier one.</summary>
        public RemoteTyping BeginTyping(string title, string initial, bool multiline, bool secret, string accent)
        {
            var t = new RemoteTyping(this, title, initial, multiline, secret, accent);
            RemoteTyping old;
            lock (gate) { old = typing; typing = t; }
            if (old != null) old.Close();
            return t;
        }

        internal void Detach(RemoteTyping t)
        {
            lock (gate) { if (typing == t) typing = null; }
        }

        /// <summary>Starts listening on the home-network address, on the port it had last time when that is free. Null when it worked.</summary>
        public string Start()
        {
            lock (gate) { if (listener != null) return null; }
            IPAddress ip = PhoneNet.PickLanAddress();
            if (ip == null) return "This PC is not on a home network (Wi-Fi or Ethernet), so a phone cannot reach it.";
            try { Listen(ip); return null; }
            catch (Exception ex) { return "Could not open the phone remote: " + ex.Message; }
        }

        internal void Listen(IPAddress ip)
        {
            TcpListener l = null;
            if (savedPort != 0)
            {
                try { l = new TcpListener(ip, savedPort); l.Start(8); }
                catch { l = null; }
            }
            if (l == null) { l = new TcpListener(ip, 0); l.Start(8); }
            lock (gate)
            {
                listener = l;
                BoundAddress = ip;
                int port = ((IPEndPoint)l.LocalEndpoint).Port;
                if (port != savedPort) { savedPort = port; Save(); }
                Url = "http://" + ip + ":" + port.ToString(CultureInfo.InvariantCulture) + "/r/" + token;
            }
            var accept = new Thread(AcceptLoop);
            accept.IsBackground = true;
            accept.Name = "LanternfallPhoneRemote";
            accept.Start(l);
        }

        public void Stop()
        {
            TcpListener l;
            lock (gate) { l = listener; listener = null; Url = null; BoundAddress = null; }
            try { if (l != null) l.Stop(); } catch { }
        }

        /// <summary>A new token: every phone that had the old address is cut off. The listener, when up, moves to the new address.</summary>
        public void Reset()
        {
            bool was = Running;
            Stop();
            lock (gate) { token = PhoneNet.NewToken(); everSeen = false; strikes.Clear(); }
            Save();
            if (was) Start();
        }

        private void AcceptLoop(object state)
        {
            TcpListener l = (TcpListener)state;
            while (true)
            {
                TcpClient c;
                try { c = l.AcceptTcpClient(); }
                catch { return; }
                if (Interlocked.Increment(ref active) > MaxConnections)
                {
                    Interlocked.Decrement(ref active);
                    try { c.Close(); } catch { }
                    continue;
                }
                var t = new Thread(HandleConnection);
                t.IsBackground = true;
                t.Start(c);
            }
        }

        private void HandleConnection(object state)
        {
            TcpClient client = (TcpClient)state;
            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 5000;
                IPAddress remote = ((IPEndPoint)client.Client.RemoteEndPoint).Address;
                NetworkStream ns = client.GetStream();
                string method, target, body;
                int bad;
                PhoneReply reply;
                if (!PhoneNet.IsAllowedClient(remote)) reply = PhoneHttp.Plain(403, "Not allowed");
                else if (!PhoneNet.TryReadRequest(new BufferedStream(ns, 4096), MaxBody, out method, out target, out body, out bad)) reply = PhoneHttp.Plain(bad, "Bad request");
                else reply = Handle(method, target, body, remote);
                PhoneHttp.Write(ns, reply);
            }
            catch { }
            finally { try { client.Close(); } catch { } Interlocked.Decrement(ref active); }
        }

        private static string Key(IPAddress a)
        {
            if (a != null && a.AddressFamily == AddressFamily.InterNetworkV6 && a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
            return a == null ? "" : a.ToString();
        }

        private bool Locked(IPAddress client)
        {
            lock (gate)
            {
                Strikes s;
                if (!strikes.TryGetValue(Key(client), out s) || !s.Locked) return false;
                if (unchecked(Environment.TickCount - s.LockedAt) > LockMilliseconds) { strikes.Remove(Key(client)); return false; }
                return true;
            }
        }

        private PhoneReply Fail(IPAddress client)
        {
            lock (gate)
            {
                int now = Environment.TickCount;
                string key = Key(client);
                Strikes s;
                if (!strikes.TryGetValue(key, out s))
                {
                    if (strikes.Count > 200) strikes.Clear();   // never grows without bound
                    s = new Strikes { Start = now };
                    strikes[key] = s;
                }
                if (unchecked(now - s.Start) > LockMilliseconds && !s.Locked) { s.Count = 0; s.Start = now; }
                s.Count++;
                if (s.Count >= MaxFailures && !s.Locked) { s.Locked = true; s.LockedAt = now; }
            }
            return PhoneHttp.Plain(404, "Not found");
        }

        /// <summary>One request. Public for the offline checks; the socket code calls it too.</summary>
        public PhoneReply Handle(string method, string target, string body, IPAddress client)
        {
            if (!PhoneNet.IsAllowedClient(client)) return PhoneHttp.Plain(403, "Not allowed");
            if (Locked(client)) return PhoneHttp.Plain(429, "Too many wrong addresses. Try again in a few minutes.");
            string path = target ?? "";
            string query = "";
            int q = path.IndexOf('?');
            if (q >= 0) { query = path.Substring(q + 1); path = path.Substring(0, q); }
            // anything outside /r/ (a phone browser asking for /favicon.ico, an icon...) is not a guess at the secret address: no strike
            if (!path.StartsWith("/r/", StringComparison.Ordinal)) return PhoneHttp.Plain(404, "Not found");
            string rest = path.Substring(3);
            int slash = rest.IndexOf('/');
            string tok = slash < 0 ? rest : rest.Substring(0, slash);
            string sub = slash < 0 ? "" : rest.Substring(slash);
            string current;
            lock (gate) current = token;
            if (!PhoneNet.FixedEquals(tok, current)) return Fail(client);
            bool post = string.Equals(method, "POST", StringComparison.Ordinal);
            bool get = string.Equals(method, "GET", StringComparison.Ordinal);

            if (sub == "" || sub == "/")
            {
                if (!get) return PhoneHttp.Plain(405, "GET only");
                Seen();
                return new PhoneReply { Type = "text/html; charset=utf-8", Body = PhoneRemotePage.Html, Page = true, Csp = PhoneRemotePage.Csp };
            }
            if (sub == "/state")
            {
                if (!get) return PhoneHttp.Plain(405, "GET only");
                Seen();
                return Json(SafeStatus());
            }
            if (sub == "/search")
            {
                if (!get) return PhoneHttp.Plain(405, "GET only");
                Seen();
                string words = QueryValue(query, "q");
                if (string.IsNullOrWhiteSpace(words) || words.Length > 200) return Json(Error("Type something to search for."));
                string err;
                List<Dictionary<string, object>> found;
                try { found = host.Search(words.Trim(), out err); }
                catch { found = null; err = "The search did not work. Try again."; }
                if (found == null) return Json(Error(err ?? "The search did not work. Try again."));
                return Json(new Dictionary<string, object> { { "ok", true }, { "results", found } });
            }
            if (sub == "/kb/text" || sub == "/kb/done" || sub == "/kb/cancel")
            {
                if (!post) return PhoneHttp.Plain(405, "POST only");
                Seen();
                RemoteTyping t;
                lock (gate) t = typing;
                if (t == null || t.IsClosed) return PhoneHttp.Plain(410, "Closed");
                if (sub == "/kb/text")
                {
                    var kd = ReadObject(body);
                    string value = kd == null ? null : Str(kd, "text");
                    if (value == null) return PhoneHttp.Plain(400, "Bad text");
                    if (!t.FromPhone(value)) return PhoneHttp.Plain(410, "Closed");
                    return Json(new Dictionary<string, object> { { "ok", true }, { "v", t.Version } });
                }
                if (!t.Finish(sub == "/kb/done")) return PhoneHttp.Plain(410, "Closed");
                return Json(Ok());
            }
            if (sub == "/pad")
            {
                if (!post) return PhoneHttp.Plain(405, "POST only");
                Seen();
                var pb = ReadObject(body);
                string button = pb == null ? null : Str(pb, "b");
                if (Array.IndexOf(PadButtons, button) < 0) return PhoneHttp.Plain(400, "Unknown button");
                string padErr = Guard(delegate { return host.Pad(button); });
                return Json(padErr == null ? Ok() : Error(padErr));
            }
            if (sub == "/games")
            {
                if (!get) return PhoneHttp.Plain(405, "GET only");
                Seen();
                string gerr = null;
                List<Dictionary<string, object>> games;
                try { games = host.Games(out gerr); }
                catch { games = null; }
                if (games == null) return Json(Error(gerr ?? "The TV could not list the games."));
                return Json(new Dictionary<string, object> { { "ok", true }, { "games", games } });
            }
            if (sub == "/launch")
            {
                if (!post) return PhoneHttp.Plain(405, "POST only");
                Seen();
                var ld = ReadObject(body);
                string gid = ld == null ? null : Str(ld, "id");
                if (gid == null || !GameId.IsMatch(gid)) return PhoneHttp.Plain(400, "Bad game");
                string lerr = Guard(delegate { return host.Launch(gid); });
                return Json(lerr == null ? Ok() : Error(lerr));
            }
            if (sub.StartsWith("/cover/", StringComparison.Ordinal))
            {
                if (!get) return PhoneHttp.Plain(405, "GET only");
                string cid = sub.Substring(7);
                if (!GameId.IsMatch(cid)) return PhoneHttp.Plain(404, "Not found");
                byte[] jpg = null;
                try { jpg = host.Cover(cid); } catch { }
                if (jpg == null) return PhoneHttp.Plain(404, "Not found");
                return new PhoneReply { Type = "image/jpeg", Bytes = jpg, MaxAge = 3600 };
            }
            if (sub == "/ptr")
            {
                if (!post) return PhoneHttp.Plain(405, "POST only");
                Seen();
                var pd = ReadObject(body);
                if (pd == null) return PhoneHttp.Plain(400, "Bad request");
                string pa = Str(pd, "a");
                if (Array.IndexOf(PointerActions, pa) < 0) return PhoneHttp.Plain(400, "Unknown action");
                int pdx = Num(pd, "dx"), pdy = Num(pd, "dy");
                string perr = Guard(delegate { return host.Pointer(pa, pdx, pdy); });
                return Json(perr == null ? Ok() : Error(perr));
            }
            if (sub == "/bctl")
            {
                if (!post) return PhoneHttp.Plain(405, "POST only");
                Seen();
                var bd = ReadObject(body);
                if (bd == null) return PhoneHttp.Plain(400, "Bad request");
                string bc = Str(bd, "cmd");
                if (Array.IndexOf(BrowseCommands, bc) < 0) return PhoneHttp.Plain(400, "Unknown command");
                string berr = Guard(delegate { return host.Browse(bc); });
                return Json(berr == null ? Ok() : Error(berr));
            }
            if (sub == "/send" || sub == "/play" || sub == "/ctl")
            {
                if (!post) return PhoneHttp.Plain(405, "POST only");
                Seen();
                var d = ReadObject(body);
                if (d == null) return PhoneHttp.Plain(400, "Bad request");
                string err;
                if (sub == "/ctl")
                {
                    string cmd = Str(d, "cmd");
                    if (Array.IndexOf(Commands, cmd) < 0) return PhoneHttp.Plain(400, "Unknown command");
                    err = Guard(delegate { return host.Control(cmd); });
                    return Json(err == null ? Ok() : Error(err));
                }
                if (sub == "/play")
                {
                    string id = Str(d, "id");
                    if (id == null || id.Length != 11 || RemoteLink.YouTubeId("https://youtu.be/" + id) != id) return PhoneHttp.Plain(400, "Bad video");
                    err = Guard(delegate { return host.PlayVideo(id); });
                    return Json(err == null ? Ok() : Error(err));
                }
                string value;
                string kind = RemoteLink.Classify(Str(d, "text"), out value);
                if (kind == RemoteLink.None) return Json(Error("Nothing to send."));
                if (kind == RemoteLink.Search)
                {
                    if (Str(d, "mode") != "web") return Json(new Dictionary<string, object> { { "ok", true }, { "kind", kind }, { "q", value } });
                    string words = value;
                    err = Guard(delegate { return host.WebSearch(words); });
                    return Json(err == null ? new Dictionary<string, object> { { "ok", true }, { "kind", "web" } } : Error(err));
                }
                string shown = value;
                err = Guard(delegate { return kind == RemoteLink.Video ? host.PlayVideo(shown) : host.OpenUrl(shown); });
                if (err != null) return Json(Error(err));
                return Json(new Dictionary<string, object> { { "ok", true }, { "kind", kind } });
            }
            return PhoneHttp.Plain(404, "Not found");
        }

        // marks the phone as present; raises PhoneConnected when it was not (first time, or back after being away)
        private void Seen()
        {
            bool arrived;
            lock (gate)
            {
                arrived = !(everSeen && unchecked(Environment.TickCount - lastPhoneSeen) < 8000);
                everSeen = true;
                lastPhoneSeen = Environment.TickCount;
            }
            if (arrived) { var h = PhoneConnected; if (h != null) { try { h(); } catch { } } }
        }

        private static string Guard(Func<string> call)
        {
            try { return call(); }
            catch { return "The TV could not do that."; }
        }

        private Dictionary<string, object> SafeStatus()
        {
            Dictionary<string, object> s = null;
            try { s = host.Status(); } catch { }
            if (s == null) s = new Dictionary<string, object>();
            if (!s.ContainsKey("ctx")) s["ctx"] = "home";
            s["ok"] = true;
            RemoteTyping t;
            lock (gate) t = typing;
            if (t != null && !t.IsClosed)
                s["kb"] = new Dictionary<string, object>
                {
                    { "v", t.Version }, { "text", t.Text }, { "title", t.Title }, { "multi", t.Multiline }, { "secret", t.Secret }, { "accent", t.Accent }
                };
            return s;
        }

        private static Dictionary<string, object> Ok() { return new Dictionary<string, object> { { "ok", true } }; }
        private static Dictionary<string, object> Error(string message) { return new Dictionary<string, object> { { "ok", false }, { "error", message } }; }

        private static PhoneReply Json(Dictionary<string, object> d)
        {
            return new PhoneReply { Body = new JavaScriptSerializer().Serialize(d) };
        }

        private static Dictionary<string, object> ReadObject(string body)
        {
            try { return new JavaScriptSerializer().DeserializeObject(body) as Dictionary<string, object>; }
            catch { return null; }
        }

        // a whole number from the body, limited to what a finger can mean (no huge jumps)
        private static int Num(Dictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return 0;
            try { return Math.Max(-600, Math.Min(600, (int)Math.Round(Convert.ToDouble(v, CultureInfo.InvariantCulture)))); }
            catch { return 0; }
        }

        private static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) ? v as string : null;
        }

        private static string QueryValue(string query, string key)
        {
            if (string.IsNullOrEmpty(query)) return null;
            foreach (string part in query.Split('&'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0 || part.Substring(0, eq) != key) continue;
                try { return Uri.UnescapeDataString(part.Substring(eq + 1).Replace('+', ' ')); } catch { return null; }
            }
            return null;
        }
    }
}
