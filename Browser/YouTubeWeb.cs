// YouTubeWeb.cs - an optional web route for a video's streams, tried when the app client gets no playable links. It runs
// a hidden Edge WebView2 page from an optional ytweb folder; the folder is not part of this repository, and without it
// Available is false and playback uses the other sources.
//
// The page is served at https://www.youtube.com/__lanternfall/ from that folder (WebResourceRequested). The WebView2 runs on its own STA thread with its own
// dispatcher, so Playnite's UI thread never waits on it (and it works outside Playnite, for tests). It shuts itself down
// after a while without use (it is a whole browser process tree). C# 5 only.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Playnite.SDK;

namespace SeleneBrowser
{
    internal static class YouTubeWeb
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private const string Prefix = "https://www.youtube.com/__lanternfall/";
        private static readonly string[] Served = { "resolver.html", "resolver.js", "youtubei.js" };

        // a failed start is not retried for this long (a crashed browser process is retried after a few seconds)
        private const int FailedBackoffSeconds = 15;

        private class Call { public readonly ManualResetEvent Done = new ManualResetEvent(false); public string Json, Error; }

        // One run of the hidden browser. Everything it owns lives here, so a session that is being torn down can never null out
        // the browser of the one that replaced it (that race left every later video without a web route: a restart right
        // after Stop failed for 40 s, then a lockout made the next videos fail as well).
        private class Session
        {
            public Thread Thread;
            public Dispatcher Disp;
            public HwndSource Host;
            public CoreWebView2Environment Env;
            public CoreWebView2Controller Controller;
            public CoreWebView2 Core;
            public DispatcherTimer Idle;
            public readonly ManualResetEvent Ready = new ManualResetEvent(false);
            public readonly ManualResetEvent Closed = new ManualResetEvent(false);
            public readonly Dictionary<string, Call> Calls = new Dictionary<string, Call>();
            public volatile string StartError = "";
            public volatile bool Stopped;
        }

        private static readonly object lk = new object();
        private static Session cur, closing;
        private static DateTime failedAt = DateTime.MinValue, lastUse = DateTime.MinValue;
        private static string lastStartError = "";

        private static string DllDir { get { return Path.GetDirectoryName(typeof(YouTubeWeb).Assembly.Location); } }

        /// <summary>The folder with resolver.html / resolver.js / youtubei.js (copied beside the DLL by build.ps1).</summary>
        public static string Folder { get { return Path.Combine(DllDir, "ytweb"); } }

        private static int availableCache = -1;   // -1 unknown, 0 no, 1 yes: the files and the runtime do not change while Playnite runs

        public static bool Available
        {
            get
            {
                int known = availableCache;
                if (known >= 0) return known == 1;
                bool ok;
                try
                {
                    ok = true;
                    foreach (string f in Served) if (!File.Exists(Path.Combine(Folder, f))) { ok = false; break; }
                    if (ok) ok = EdgeView.IsAvailable();
                }
                catch { ok = false; }
                availableCache = ok ? 1 : 0;
                return ok;
            }
        }

        /// <summary>
        /// A player-response-shaped JSON for videoId (playabilityStatus, videoDetails, streamingData.adaptiveFormats with
        /// ready urls, captions), or null with the reason in error. Blocks up to timeoutMs: never call it on a UI thread.
        /// </summary>
        public static string Resolve(string videoId, int timeoutMs, out string error)
        {
            error = "";
            // waiting here on Playnite's UI thread (or our own) would freeze it; any other thread may wait (a pool thread can
            // carry a Dispatcher too, so that alone proves nothing)
            Dispatcher here = Dispatcher.FromThread(Thread.CurrentThread);
            var app = System.Windows.Application.Current;
            if (here != null && (here == CurrentDisp() || (app != null && here == app.Dispatcher))) { error = "called on a UI thread"; return null; }
            DateTime until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            Session s, prev = null;
            bool fresh = false;
            lock (lk)
            {
                if ((DateTime.UtcNow - failedAt).TotalSeconds < FailedBackoffSeconds) { error = "web route failed to start: " + lastStartError; return null; }
                lastUse = DateTime.UtcNow;
                s = cur;
                if (s == null || s.Stopped)
                {
                    prev = closing;
                    s = new Session();
                    cur = s;
                    fresh = true;
                }
            }
            if (fresh)
            {
                // the previous browser may still be closing (same user data folder): give it a moment, not forever
                if (prev != null) prev.Closed.WaitOne(Math.Min(8000, Remaining(until) / 2));
                BeginSession(s);
            }
            if (!s.Ready.WaitOne(Remaining(until)) || s.Core == null)
            {
                string why = s.StartError.Length > 0 ? s.StartError : "page did not load";
                lock (lk) { lastStartError = why; failedAt = DateTime.UtcNow; }
                StopSession(s);
                error = "web route failed to start: " + why;
                return null;
            }
            string id = Guid.NewGuid().ToString("N");
            Call c = new Call();
            lock (lk) s.Calls[id] = c;
            try
            {
                Dispatcher d = s.Disp;
                if (d == null || s.Stopped) { error = "web route stopped"; return null; }
                d.BeginInvoke(new Action(delegate
                {
                    try
                    {
                        lastUse = DateTime.UtcNow;
                        if (s.Core == null) throw new InvalidOperationException("web route stopped");
                        s.Core.ExecuteScriptAsync("window.lfResolve(" + Jx.ToJson(id) + "," + Jx.ToJson(videoId) + ")");
                    }
                    catch (Exception ex) { c.Error = ex.Message; c.Done.Set(); }
                }));
                if (!c.Done.WaitOne(Remaining(until))) { error = "timed out"; return null; }
                if (c.Json == null) { error = c.Error ?? "failed"; return null; }
                return c.Json;
            }
            finally { lock (lk) s.Calls.Remove(id); }
        }

        private static Dispatcher CurrentDisp()
        {
            lock (lk) { return cur == null ? null : cur.Disp; }
        }

        private static int Remaining(DateTime until)
        {
            double ms = (until - DateTime.UtcNow).TotalMilliseconds;
            return ms < 0 ? 0 : (int)ms;
        }

        private static void BeginSession(Session s)
        {
            var started = new ManualResetEvent(false);
            s.Thread = new Thread(delegate ()
            {
                s.Disp = Dispatcher.CurrentDispatcher;
                started.Set();
                s.Disp.BeginInvoke(new Action(delegate { Start(s); }));
                Dispatcher.Run();
                s.Closed.Set();
            });
            s.Thread.Name = "Lanternfall YouTube web";
            s.Thread.IsBackground = true;
            s.Thread.SetApartmentState(ApartmentState.STA);
            s.Thread.Start();
            started.WaitOne(5000);
        }

        // on the session's thread
        private static async void Start(Session s)
        {
            try
            {
                EdgeView.EnsureLoaderPath();
                // a hidden 1x1 popup window parents the WebView2 (never shown)
                var p = new HwndSourceParameters("Lanternfall YouTube web") { Width = 1, Height = 1, PositionX = -32000, PositionY = -32000 };
                p.WindowStyle = unchecked((int)0x80000000);   // WS_POPUP, not WS_VISIBLE
                p.ExtendedWindowStyle = 0x80 | 0x08000000;     // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
                s.Host = new HwndSource(p);
                string dataRoot = SeleneBrowserPlugin.DataDir;
                if (string.IsNullOrEmpty(dataRoot)) dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lanternfall");
                string udf = Path.Combine(dataRoot, "webview2-youtube");
                Directory.CreateDirectory(udf);
                s.Env = await CoreWebView2Environment.CreateAsync(null, udf, new CoreWebView2EnvironmentOptions());
                if (s.Stopped) { ReleaseStopped(s); return; }
                s.Controller = await s.Env.CreateCoreWebView2ControllerAsync(s.Host.Handle);
                // the session was stopped while the browser was still being created: StopSession ran before there was a controller
                // to close, so close it here or its browser process keeps the user-data folder locked
                if (s.Stopped) { ReleaseStopped(s); return; }
                s.Controller.IsVisible = false;
                CoreWebView2 core = s.Controller.CoreWebView2;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreDefaultScriptDialogsEnabled = false;
                core.AddWebResourceRequestedFilter(Prefix + "*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += delegate (object o, CoreWebView2WebResourceRequestedEventArgs e) { OnResource(s, e); };
                core.WebMessageReceived += delegate (object o, CoreWebView2WebMessageReceivedEventArgs e) { OnMessage(s, e); };
                core.ProcessFailed += delegate (object o, CoreWebView2ProcessFailedEventArgs e)
                {
                    logger.Warn("SeleneBrowser: YouTube web route process failed (" + e.ProcessFailedKind + ")");
                    FailAll(s, "the web route's browser process stopped");
                    lock (lk) { lastStartError = "browser process stopped"; failedAt = DateTime.UtcNow.AddSeconds(3 - FailedBackoffSeconds); }
                    StopSession(s);
                };
                core.NavigationCompleted += delegate (object o, CoreWebView2NavigationCompletedEventArgs e)
                {
                    if (!e.IsSuccess) s.StartError = "page failed (" + e.WebErrorStatus + ")";
                };
                s.Core = core;
                s.Idle = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
                s.Idle.Tick += delegate
                {
                    bool busy;
                    lock (lk) busy = s.Calls.Count > 0;
                    if (!busy && (DateTime.UtcNow - lastUse).TotalMinutes > 15) { logger.Info("SeleneBrowser: YouTube web route idle, closed"); StopSession(s); }
                };
                s.Idle.Start();
                core.Navigate(Prefix + "resolver.html");
            }
            catch (Exception ex)
            {
                logger.Warn("SeleneBrowser: YouTube web route start: " + ex.Message);
                s.StartError = ex.Message;
                s.Core = null;
                s.Ready.Set();   // wakes the waiter, which then finds the error
                StopSession(s);
            }
        }

        private static void OnResource(Session s, CoreWebView2WebResourceRequestedEventArgs e)
        {
            try
            {
                string path = new Uri(e.Request.Uri).AbsolutePath;   // /__lanternfall/<name>
                string name = path.Substring(path.LastIndexOf('/') + 1);
                if (Array.IndexOf(Served, name) < 0)
                {
                    e.Response = s.Env.CreateWebResourceResponse(null, 404, "Not Found", "Content-Type: text/plain");
                    return;
                }
                byte[] bytes = File.ReadAllBytes(Path.Combine(Folder, name));
                string type = name.EndsWith(".html", StringComparison.Ordinal) ? "text/html; charset=utf-8" : "text/javascript; charset=utf-8";
                e.Response = s.Env.CreateWebResourceResponse(new MemoryStream(bytes), 200, "OK", "Content-Type: " + type + "\r\nCache-Control: no-store");
            }
            catch (Exception ex) { logger.Warn("SeleneBrowser: YouTube web route file: " + ex.Message); }
        }

        private static void OnMessage(Session s, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                object m = Jx.Parse(e.TryGetWebMessageAsString());
                if (Jx.Bool(m, "ready")) { logger.Info("SeleneBrowser: YouTube web route ready"); s.Ready.Set(); return; }
                string id = Jx.Str(m, "id");
                Call c;
                lock (lk) s.Calls.TryGetValue(id, out c);
                if (c == null) return;
                if (Jx.Bool(m, "ok")) c.Json = Jx.Str(m, "json");
                else c.Error = Jx.Str(m, "error");
                c.Done.Set();
            }
            catch (Exception ex) { logger.Warn("SeleneBrowser: YouTube web route message: " + ex.Message); }
        }

        private static void FailAll(Session s, string why)
        {
            lock (lk) foreach (Call c in s.Calls.Values) { if (c.Json == null) c.Error = why; c.Done.Set(); }
        }

        // on the session's thread: what StopSession would have closed, for a session stopped before its browser existed
        private static void ReleaseStopped(Session s)
        {
            try { if (s.Controller != null) s.Controller.Close(); } catch { }
            try { if (s.Host != null) s.Host.Dispose(); } catch { }
            s.Core = null;
        }

        private static void StopSession(Session s)
        {
            lock (lk)
            {
                if (s.Stopped) return;
                s.Stopped = true;
                if (cur == s) { cur = null; closing = s; }
            }
            Dispatcher d = s.Disp;
            if (d == null) { s.Closed.Set(); s.Ready.Set(); FailAll(s, "web route stopped"); return; }
            try
            {
                d.BeginInvoke(new Action(delegate
                {
                    try { if (s.Idle != null) s.Idle.Stop(); } catch { }
                    try { if (s.Controller != null) s.Controller.Close(); } catch { }
                    try { if (s.Host != null) s.Host.Dispose(); } catch { }
                    s.Core = null;
                    s.Ready.Set();
                    FailAll(s, "web route stopped");
                    Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }));
            }
            catch { s.Closed.Set(); s.Ready.Set(); FailAll(s, "web route stopped"); }
        }
    }
}
