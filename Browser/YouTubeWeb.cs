// YouTubeWeb.cs - YouTube's own web route for a video's streams, used when YouTube refuses the plain app client
// (ANDROID_VR answers "Sign in to confirm you're not a bot" and app-client links stop after ~1 MB). Same idea as FreeTube:
// a hidden Edge WebView2 page runs YouTube's BotGuard to mint Proof-of-Origin tokens, asks the TV client for the video and
// deciphers its stream urls with YouTube.js (lib/ytweb, MIT). The page script is ytweb/resolver.js.
//
// The page is ours but lives at https://www.youtube.com/__lanternfall/ (served from the ytweb folder through
// WebResourceRequested), so YouTube's endpoints are same-origin. The WebView2 runs on its own STA thread with its own
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

        private class Call { public readonly ManualResetEvent Done = new ManualResetEvent(false); public string Json, Error; }

        private static readonly object lk = new object();
        private static Thread thread;
        private static Dispatcher disp;
        private static HwndSource host;
        private static CoreWebView2Environment env;
        private static CoreWebView2Controller controller;
        private static CoreWebView2 core;
        private static ManualResetEvent ready = new ManualResetEvent(false);
        private static string startError = "";
        private static DateTime failedAt = DateTime.MinValue, lastUse = DateTime.MinValue;
        private static DispatcherTimer idle;
        private static readonly Dictionary<string, Call> calls = new Dictionary<string, Call>();

        private static string DllDir { get { return Path.GetDirectoryName(typeof(YouTubeWeb).Assembly.Location); } }

        /// <summary>The folder with resolver.html / resolver.js / youtubei.js (copied beside the DLL by build.ps1).</summary>
        public static string Folder { get { return Path.Combine(DllDir, "ytweb"); } }

        public static bool Available
        {
            get
            {
                try
                {
                    foreach (string f in Served) if (!File.Exists(Path.Combine(Folder, f))) return false;
                    return EdgeView.IsAvailable();
                }
                catch { return false; }
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
            if (here != null && (here == disp || (app != null && here == app.Dispatcher))) { error = "called on a UI thread"; return null; }
            DateTime until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            lock (lk)
            {
                if ((DateTime.UtcNow - failedAt).TotalSeconds < 60) { error = "web route failed to start: " + startError; return null; }
                lastUse = DateTime.UtcNow;
                EnsureThread();
            }
            if (!ready.WaitOne(Remaining(until)))
            {
                lock (lk) { if (startError.Length == 0) startError = "page did not load"; failedAt = DateTime.UtcNow; }
                Stop();
                error = "web route failed to start: " + startError;
                return null;
            }
            string id = Guid.NewGuid().ToString("N");
            Call c = new Call();
            lock (lk) calls[id] = c;
            try
            {
                Dispatcher d = disp;
                if (d == null) { error = "web route stopped"; return null; }
                d.BeginInvoke(new Action(delegate
                {
                    try
                    {
                        lastUse = DateTime.UtcNow;
                        if (core == null) throw new InvalidOperationException("web route stopped");
                        core.ExecuteScriptAsync("window.lfResolve(" + Jx.ToJson(id) + "," + Jx.ToJson(videoId) + ")");
                    }
                    catch (Exception ex) { c.Error = ex.Message; c.Done.Set(); }
                }));
                if (!c.Done.WaitOne(Remaining(until))) { error = "timed out"; return null; }
                if (c.Json == null) { error = c.Error ?? "failed"; return null; }
                return c.Json;
            }
            finally { lock (lk) calls.Remove(id); }
        }

        private static int Remaining(DateTime until)
        {
            double ms = (until - DateTime.UtcNow).TotalMilliseconds;
            return ms < 0 ? 0 : (int)ms;
        }

        // under lk
        private static void EnsureThread()
        {
            if (thread != null && thread.IsAlive) return;
            ready.Reset();
            startError = "";
            var started = new ManualResetEvent(false);
            thread = new Thread(delegate ()
            {
                disp = Dispatcher.CurrentDispatcher;
                started.Set();
                disp.BeginInvoke(new Action(Start));
                Dispatcher.Run();
            });
            thread.Name = "Lanternfall YouTube web";
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            started.WaitOne(5000);
        }

        // on the web thread
        private static async void Start()
        {
            try
            {
                EdgeView.EnsureLoaderPath();
                // a hidden 1x1 popup window parents the WebView2 (never shown)
                var p = new HwndSourceParameters("Lanternfall YouTube web") { Width = 1, Height = 1, PositionX = -32000, PositionY = -32000 };
                p.WindowStyle = unchecked((int)0x80000000);   // WS_POPUP, not WS_VISIBLE
                p.ExtendedWindowStyle = 0x80 | 0x08000000;     // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
                host = new HwndSource(p);
                string dataRoot = SeleneBrowserPlugin.DataDir;
                if (string.IsNullOrEmpty(dataRoot)) dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nomad");
                string udf = Path.Combine(dataRoot, "webview2-youtube");
                Directory.CreateDirectory(udf);
                env = await CoreWebView2Environment.CreateAsync(null, udf, new CoreWebView2EnvironmentOptions());
                controller = await env.CreateCoreWebView2ControllerAsync(host.Handle);
                controller.IsVisible = false;
                core = controller.CoreWebView2;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreDefaultScriptDialogsEnabled = false;
                core.AddWebResourceRequestedFilter(Prefix + "*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += OnResource;
                core.WebMessageReceived += OnMessage;
                core.ProcessFailed += delegate (object s, CoreWebView2ProcessFailedEventArgs e)
                {
                    logger.Warn("SeleneBrowser: YouTube web route process failed (" + e.ProcessFailedKind + ")");
                    FailAll("the web route's browser process stopped");
                    lock (lk) { startError = "browser process stopped"; failedAt = DateTime.UtcNow.AddSeconds(-50); }
                    Stop();
                };
                core.NavigationCompleted += delegate (object s, CoreWebView2NavigationCompletedEventArgs e)
                {
                    if (!e.IsSuccess) { lock (lk) startError = "page failed (" + e.WebErrorStatus + ")"; }
                };
                idle = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
                idle.Tick += delegate
                {
                    bool busy;
                    lock (lk) busy = calls.Count > 0;
                    if (!busy && (DateTime.UtcNow - lastUse).TotalMinutes > 15) { logger.Info("SeleneBrowser: YouTube web route idle, closed"); Stop(); }
                };
                idle.Start();
                core.Navigate(Prefix + "resolver.html");
            }
            catch (Exception ex)
            {
                logger.Warn("SeleneBrowser: YouTube web route start: " + ex.Message);
                lock (lk) { startError = ex.Message; failedAt = DateTime.UtcNow; }
                ready.Set();   // wakes the waiter, which then finds core == null / the error
                Stop();
            }
        }

        private static void OnResource(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            try
            {
                string path = new Uri(e.Request.Uri).AbsolutePath;   // /__lanternfall/<name>
                string name = path.Substring(path.LastIndexOf('/') + 1);
                if (Array.IndexOf(Served, name) < 0)
                {
                    e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", "Content-Type: text/plain");
                    return;
                }
                byte[] bytes = File.ReadAllBytes(Path.Combine(Folder, name));
                string type = name.EndsWith(".html", StringComparison.Ordinal) ? "text/html; charset=utf-8" : "text/javascript; charset=utf-8";
                e.Response = env.CreateWebResourceResponse(new MemoryStream(bytes), 200, "OK", "Content-Type: " + type + "\r\nCache-Control: no-store");
            }
            catch (Exception ex) { logger.Warn("SeleneBrowser: YouTube web route file: " + ex.Message); }
        }

        private static void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                object m = Jx.Parse(e.TryGetWebMessageAsString());
                if (Jx.Bool(m, "ready")) { logger.Info("SeleneBrowser: YouTube web route ready"); ready.Set(); return; }
                string id = Jx.Str(m, "id");
                Call c;
                lock (lk) calls.TryGetValue(id, out c);
                if (c == null) return;
                if (Jx.Bool(m, "ok")) c.Json = Jx.Str(m, "json");
                else c.Error = Jx.Str(m, "error");
                c.Done.Set();
            }
            catch (Exception ex) { logger.Warn("SeleneBrowser: YouTube web route message: " + ex.Message); }
        }

        private static void FailAll(string why)
        {
            lock (lk) foreach (Call c in calls.Values) { if (c.Json == null) c.Error = why; c.Done.Set(); }
        }

        /// <summary>Closes the hidden WebView2 and ends its thread (any thread). The next Resolve starts it again.</summary>
        public static void Stop()
        {
            Dispatcher d;
            lock (lk) { d = disp; disp = null; thread = null; }
            if (d == null) return;
            try
            {
                d.BeginInvoke(new Action(delegate
                {
                    try { if (idle != null) idle.Stop(); } catch { }
                    try { if (controller != null) controller.Close(); } catch { }
                    try { if (host != null) host.Dispose(); } catch { }
                    controller = null; core = null; env = null; host = null; idle = null;
                    FailAll("web route stopped");
                    Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }));
            }
            catch { }
        }
    }
}
