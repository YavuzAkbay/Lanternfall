// EdgeView.cs - IPageView on Edge WebView2 (WebView2CompositionControl, so WPF overlays stay visible above the page).
// Unlike CEF in Playnite, this has H.264/AAC. All WebView2 type references live in this file so a missing
// DLL only fails here (callers probe with EdgeView.IsAvailable() from a NoInlining method).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Playnite.SDK;

namespace SeleneBrowser
{
    public partial class EdgeView : UserControl, IPageView
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly object envLock = new object();
        private static Task<CoreWebView2Environment> envTask;
        private static bool loaderPathSet;

        private readonly HashSet<string> logged = new HashSet<string>();
        private readonly IWebView2 web;   // WebView2CompositionControl, or the windowed WebView2 for the video player
        private CoreWebView2 core;
        private string pendingUrl;
        private double zoomLevel;
        private double baseScale = 1;
        private bool initFailed;
        private readonly bool windowed;
        private readonly bool isPlayer;   // windowed YouTube player: no ads, unlike the windowed browser
        private bool disposed;
        private static int live;   // views alive; the shared environment is dropped with the last one

        // SetIdle can arrive before the core exists; remember it and apply it in OnInitCompleted
        private bool idleRequested, idleSet;
        // trusted CDP input coalescing (CDP is a cross-process IPC issued on the UI thread)
        private const int MouseIntervalMs = 25;
        private const int MouseResyncMs = 100;
        private const int WheelIntervalMs = 30;
        private double lastMouseX, lastMouseY;
        private int lastMouseAt;
        private int wheelDeltaX, wheelDeltaY;
        private double wheelX, wheelY;
        private int lastWheelAt;
        private readonly DispatcherTimer wheelFlush;
        private readonly DispatcherTimer moveFlush;   // sends the last coalesced pointer move
        private double pendingX, pendingY;
        private bool pendingMove;

        /// <summary>Raised once when WebView2 could not start; the host switches to another engine.</summary>
        public event Action InitFailed;

        private void SetFailed()
        {
            if (initFailed || disposed) return;
            initFailed = true;
            var h = InitFailed;
            if (h != null) h();
        }

        public event Action<string> LoadEnd;
        public event Action<string> AddressChanged;
        public event Action<string> TitleChanged;
        public event EventHandler FullScreenChanged;
        public event Action<string> NewWindowRequested;
        public event Action<string> PageNotice;
        private int lastCrashAt;

        public UIElement Element { get { return this; } }

        // ----- availability / environment -----

        internal static void EnsureLoaderPath()
        {
            lock (envLock)
            {
                if (loaderPathSet) return;
                loaderPathSet = true;
                try { CoreWebView2Environment.SetLoaderDllFolderPath(Path.GetDirectoryName(typeof(EdgeView).Assembly.Location)); }
                catch (Exception ex) { logger.Warn("SeleneBrowser: EdgeView SetLoaderDllFolderPath: " + ex.Message); }
            }
        }

        /// <summary>True when the WebView2 runtime can be used. Never throws.</summary>
        public static bool IsAvailable()
        {
            try
            {
                EnsureLoaderPath();
                return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
            }
            catch (Exception) { return false; }
        }

        /// <summary>Installed runtime version, or "" .</summary>
        public static string RuntimeVersion()
        {
            try { return CoreWebView2Environment.GetAvailableBrowserVersionString() ?? ""; }
            catch (Exception) { return ""; }
        }

        private static Task<CoreWebView2Environment> GetEnvironment()
        {
            lock (envLock)
            {
                // a failed environment is not kept: one hiccup would otherwise break every player until Playnite restarts
                if (envTask != null && (envTask.IsFaulted || envTask.IsCanceled)) envTask = null;
                if (envTask == null)
                {
                    EnsureLoaderPath();
                    string udf = Path.Combine(SeleneBrowserPlugin.DataDir, "webview2");
                    var options = new CoreWebView2EnvironmentOptions();
                    options.AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required";
                    try { options.AreBrowserExtensionsEnabled = true; AdBlock.StartExtensionFetch(); }   // uBlock Origin Lite, best effort
                    catch (Exception ex) { logger.Warn("SeleneBrowser: extensions option: " + ex.Message); }
                    var ui = TaskScheduler.FromCurrentSynchronizationContext();
                    // an older runtime that rejects the extensions option: retry plain
                    envTask = CoreWebView2Environment.CreateAsync(null, udf, options).ContinueWith(delegate(Task<CoreWebView2Environment> t)
                    {
                        if (!t.IsFaulted) return t;
                        logger.Warn("SeleneBrowser: WebView2 environment with extensions failed, retrying: " + t.Exception.GetBaseException().Message);
                        var plain = new CoreWebView2EnvironmentOptions();
                        plain.AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required";
                        return CoreWebView2Environment.CreateAsync(null, udf, plain);
                    }, ui).Unwrap();
                }
                return envTask;
            }
        }

        // ----- creation -----

        public EdgeView() : this(false, false) { }

        public EdgeView(bool windowed) : this(windowed, false) { }

        public EdgeView(bool windowed, bool player)
        {
            Focusable = false;
            this.windowed = windowed;
            this.isPlayer = player;
            live++;
            lastMouseAt = Environment.TickCount - MouseResyncMs;   // first move always goes through
            lastWheelAt = Environment.TickCount - WheelIntervalMs;
            wheelFlush = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(WheelIntervalMs) };
            wheelFlush.Tick += delegate { wheelFlush.Stop(); SendWheel(Environment.TickCount); };
            moveFlush = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(MouseIntervalMs + 5) };
            moveFlush.Tick += delegate
            {
                moveFlush.Stop();
                if (!pendingMove || disposed) return;
                pendingMove = false;
                lastMouseX = pendingX; lastMouseY = pendingY; lastMouseAt = Environment.TickCount;
                Cdp("mouseMove", "Input.dispatchMouseEvent", Mouse("mouseMoved", pendingX, pendingY, ""));
            };
            if (windowed)
            {
                var wv = new WebView2();
                web = wv;
                Content = wv;
            }
            else
            {
                // (no view is created this way any more: pages and the player are windowed, which is native-sharp)
                web = new WebView2CompositionControl();
                Content = web;
            }
            ApplyBackground();
            themeHook = delegate { ApplyBackground(); };
            Theme.Changed += themeHook;   // the picked Lanternfall theme changed: the page's loading color follows
            web.CoreWebView2InitializationCompleted += OnInitCompleted;
            try
            {
                var ui = TaskScheduler.FromCurrentSynchronizationContext();
                GetEnvironment().ContinueWith(delegate(Task<CoreWebView2Environment> t)
                {
                    if (disposed) return;
                    if (t.IsFaulted || t.IsCanceled)
                    {
                        SetFailed();
                        Fail("environment", t.Exception == null ? null : t.Exception.GetBaseException());
                        return;
                    }
                    try { web.EnsureCoreWebView2Async(t.Result); }
                    catch (Exception ex) { SetFailed(); Fail("init", ex); }
                }, ui);
            }
            catch (Exception ex) { SetFailed(); Fail("init", ex); }
        }

        private Action themeHook;

        // what shows before a page paints: the theme's base color (the backdrop itself is a gradient), not white
        private void ApplyBackground()
        {
            if (disposed) return;
            try
            {
                var bg = Theme.Background as SolidColorBrush;
                System.Windows.Media.Color c = bg != null ? bg.Color : Theme.ScrimColor;
                web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, c.R, c.G, c.B);
            }
            catch (Exception ex) { Fail("background", ex); }
        }

        private void OnInitCompleted(object sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            if (!e.IsSuccess) { SetFailed(); Fail("init", e.InitializationException); return; }
            try
            {
                core = web.CoreWebView2;
                ApplyBackground();
                // Geometry was fixed before EnsureCoreWebView2Async; only the zoom is applied here.
                var s = core.Settings;
                s.AreDefaultContextMenusEnabled = false;
                s.IsStatusBarEnabled = false;
                s.IsZoomControlEnabled = false;
                s.AreBrowserAcceleratorKeysEnabled = false;
                core.NewWindowRequested += delegate(object o, CoreWebView2NewWindowRequestedEventArgs a)
                {
                    a.Handled = true;
                    // a window the page opens on its own (ads, trackers) is not a link the user pressed: drop it
                    if (!a.IsUserInitiated) return;
                    string nu = a.Uri;
                    // an extension (uBOL onboarding page) must not take over the page; a real pop-up becomes a new tab
                    if (!string.IsNullOrEmpty(nu) && !nu.StartsWith("extension://", StringComparison.OrdinalIgnoreCase)
                        && !nu.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)) Raise(NewWindowRequested, nu);
                };
                core.DownloadStarting += OnDownloadStarting;   // (EdgeView.Extras.cs: asked on screen first; the video player refuses them)
                ExtrasInit();
                // a page's own dialogs cannot be answered with the pad: an alert is dismissed (and shown as a message), a question is
                // declined (left unanswered = cancelled), leaving a page is allowed. Without this the page waits for a click that
                // never comes.
                core.ScriptDialogOpening += delegate(object o, CoreWebView2ScriptDialogOpeningEventArgs a)
                {
                    try
                    {
                        string m = (a.Message ?? "").Trim();
                        if (m.Length > 90) m = m.Substring(0, 89) + "\u2026";
                        if (a.Kind == CoreWebView2ScriptDialogKind.Alert) { a.Accept(); Raise(PageNotice, m.Length > 0 ? "Page: " + m : "The page showed a message"); }
                        else if (a.Kind == CoreWebView2ScriptDialogKind.Beforeunload) a.Accept();
                        else Raise(PageNotice, "The page asked a question and it was declined" + (m.Length > 0 ? ": " + m : ""));
                    }
                    catch (Exception ex) { Fail("dialog", ex); }
                };
                core.ProcessFailed += delegate(object o, CoreWebView2ProcessFailedEventArgs a)
                {
                    try
                    {
                        if (a.ProcessFailedKind != CoreWebView2ProcessFailedKind.RenderProcessExited && a.ProcessFailedKind != CoreWebView2ProcessFailedKind.RenderProcessUnresponsive
                            && a.ProcessFailedKind != CoreWebView2ProcessFailedKind.BrowserProcessExited) return;
                        int now = Environment.TickCount;
                        if (unchecked(now - lastCrashAt) < 10000) { Raise(PageNotice, "The page keeps failing"); return; }   // no reload loop
                        lastCrashAt = now;
                        Raise(PageNotice, "The page stopped responding - reloading");
                        if (a.ProcessFailedKind != CoreWebView2ProcessFailedKind.BrowserProcessExited) core.Reload();
                    }
                    catch (Exception ex) { Fail("process failed", ex); }
                };
                core.PermissionRequested += delegate(object o, CoreWebView2PermissionRequestedEventArgs a)
                {
                    a.State = CoreWebView2PermissionState.Deny;
                    Raise(PageNotice, "The page asked for a permission (denied)");
                };
                core.NavigationCompleted += delegate(object o, CoreWebView2NavigationCompletedEventArgs a)
                {
                    // a request that was cancelled (a phone site switching its user agent and opening the page again) is not a finished page
                    if (!a.IsSuccess && a.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled) return;
                    Raise(LoadEnd, core.Source);
                };
                core.SourceChanged += delegate { Raise(AddressChanged, core.Source); };
                core.DocumentTitleChanged += delegate { Raise(TitleChanged, core.DocumentTitle); };
                core.ContainsFullScreenElementChanged += delegate { RaiseFullScreen(); };
                // element fullscreen is emulated in the page (see EdgeFullscreen.cs), reported back as a web message
                core.AddScriptToExecuteOnDocumentCreatedAsync(EdgeFullscreen.Script);
                core.WebMessageReceived += delegate(object o, CoreWebView2WebMessageReceivedEventArgs a)
                {
                    string m;
                    try { m = a.TryGetWebMessageAsString(); } catch (Exception) { return; }
                    if (m != null && m.StartsWith("lfscroll:", StringComparison.Ordinal)) { RaiseScrollMoved(m); return; }   // how far the page really scrolled (EdgeView.Extras.cs)
                    if (m == null || !m.StartsWith(EdgeFullscreen.Message)) return;
                    bool on = m.EndsWith("1");
                    if (on != pageFullScreen) { pageFullScreen = on; RaiseFullScreen(); }
                };
                core.NavigationStarting += delegate(object o, CoreWebView2NavigationStartingEventArgs a)
                {
                    if (pageFullScreen) { pageFullScreen = false; RaiseFullScreen(); }
                    if (LauncherLink(a.Uri)) { a.Cancel = true; if (a.IsUserInitiated) OpenLauncher(a.Uri); return; }
                    MobileSwitchNeeded(a);   // a site remembered as a phone site (EdgeView.Extras.cs)
                };
                if (!isPlayer) { AdBlock.Attach(core); AdBlock.InstallExtension(core); }   // the video player has no ads
                ApplyZoom();
                if (!string.IsNullOrEmpty(pendingUrl)) { string u = pendingUrl; pendingUrl = null; core.Navigate(u); }
                ApplyIdle();   // a SetIdle made before the core was ready is applied here
            }
            catch (Exception ex) { Fail("setup", ex); }
        }

        // ----- state -----

        public bool IsReady { get { return core != null; } }
        private bool pageFullScreen;
        public bool IsFullScreen { get { try { return pageFullScreen || (core != null && core.ContainsFullScreenElement); } catch (Exception) { return pageFullScreen; } } }

        private void RaiseFullScreen()
        {
            var h = FullScreenChanged;
            if (h != null) h(this, EventArgs.Empty);
        }
        public string Address
        {
            get
            {
                if (core == null) return pendingUrl ?? "";
                try { return core.Source ?? ""; } catch (Exception) { return ""; }
            }
        }
        public string Title
        {
            get
            {
                if (core == null) return "";
                try { return core.DocumentTitle ?? ""; } catch (Exception) { return ""; }
            }
        }
        public bool CanGoBack { get { try { return core != null && core.CanGoBack; } catch (Exception) { return false; } } }

        public void Navigate(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            if (core == null) { pendingUrl = url; return; }
            try { core.Navigate(url); }
            catch (Exception ex) { Fail("Navigate", ex); }
        }

        public void GoBack() { try { if (core != null && core.CanGoBack) core.GoBack(); } catch (Exception ex) { Fail("GoBack", ex); } }
        public void GoForward() { try { if (core != null && core.CanGoForward) core.GoForward(); } catch (Exception ex) { Fail("GoForward", ex); } }
        public void Reload() { try { if (core != null) core.Reload(); } catch (Exception ex) { Fail("Reload", ex); } }

        // ----- scripting -----

        public void ExecuteScript(string js)
        {
            if (core == null || string.IsNullOrEmpty(js)) return;
            try { core.ExecuteScriptAsync(js); }
            catch (Exception ex) { Fail("ExecuteScript", ex); }
        }

        public void EvaluateAsync(string js, Action<string> onResult)
        {
            if (core == null) { Post(onResult, null); return; }
            try
            {
                core.ExecuteScriptAsync(js).ContinueWith(delegate(Task<string> t)
                {
                    string r = null;
                    if (!t.IsFaulted && !t.IsCanceled) r = Decode(t.Result);
                    else if (t.Exception != null) Fail("EvaluateAsync", t.Exception.GetBaseException());
                    Post(onResult, r);
                });
            }
            catch (Exception ex) { Fail("EvaluateAsync", ex); Post(onResult, null); }
        }

        // JSON text from ExecuteScriptAsync -> what CEF returned (raw value as string)
        private static string Decode(string json)
        {
            if (json == null) return null;
            json = json.Trim();
            if (json.Length == 0 || json == "null" || json == "undefined") return null;
            if (json[0] == '"')
            {
                try { return new JavaScriptSerializer().Deserialize<string>(json); }
                catch (Exception) { return json; }
            }
            return json;
        }

        // ----- trusted input (DevTools protocol) -----

        // The page's CSS pixels relative to this view: the user's zoom.
        private double Z { get { double z = 1; try { z = web.ZoomFactor; } catch (Exception) { } return z <= 0 ? 1 : z; } }

        private static string N(double v) { return v.ToString("0.##", CultureInfo.InvariantCulture); }

        private string Mouse(string type, double x, double y, string extra)
        {
            double z = Z;
            return "{\"type\":\"" + type + "\",\"x\":" + N(x / z) + ",\"y\":" + N(y / z) + (extra ?? "") + "}";
        }

        public void MoveMouse(double x, double y)
        {
            // CDP input is a cross-process IPC issued on the UI thread and the pad poll can call this
            // every 16-30 ms. Coalesce sub-pixel / too-frequent moves, but always resync after a short
            // gap so the cursor never feels stuck. Click() bypasses this and still sends its own move.
            int now = Environment.TickCount;
            int gap = unchecked(now - lastMouseAt);
            if (gap < MouseResyncMs)
            {
                if (Math.Abs(x - lastMouseX) < 0.5 && Math.Abs(y - lastMouseY) < 0.5) return;
                if (gap < MouseIntervalMs)
                {
                    pendingX = x; pendingY = y; pendingMove = true;   // the burst's last position still goes out
                    if (!moveFlush.IsEnabled) moveFlush.Start();
                    return;
                }
            }
            pendingMove = false;
            moveFlush.Stop();
            lastMouseX = x;
            lastMouseY = y;
            lastMouseAt = now;
            Cdp("mouseMove", "Input.dispatchMouseEvent", Mouse("mouseMoved", x, y, ""));
        }

        public void Click(double x, double y)
        {
            if (core == null) return;
            Cdp("mouseMove", "Input.dispatchMouseEvent", Mouse("mouseMoved", x, y, ""));
            string b = ",\"button\":\"left\",\"clickCount\":1";
            Cdp("click", "Input.dispatchMouseEvent", Mouse("mousePressed", x, y, b + ",\"buttons\":1"));
            Cdp("click", "Input.dispatchMouseEvent", Mouse("mouseReleased", x, y, b));
        }

        public void Wheel(double x, double y, int deltaY) { Wheel(x, y, deltaY, 0); }

        public void Wheel(double x, double y, int deltaY, int deltaX)
        {
            // CDP is a cross-process IPC on the UI thread: coalesce a wheel burst into at most one
            // call per ~30 ms. Sum the deltas so the tail of a burst is never lost, and flush at once
            // when the direction reverses so edge scrolling can change direction immediately.
            bool reverse = (deltaY != 0 && wheelDeltaY != 0 && (deltaY > 0) != (wheelDeltaY > 0))
                        || (deltaX != 0 && wheelDeltaX != 0 && (deltaX > 0) != (wheelDeltaX > 0));
            wheelDeltaY += deltaY;
            wheelDeltaX += deltaX;
            wheelX = x;
            wheelY = y;
            int now = Environment.TickCount;
            if (reverse || unchecked(now - lastWheelAt) >= WheelIntervalMs)
            {
                wheelFlush.Stop();
                SendWheel(now);
            }
            else if (!wheelFlush.IsEnabled) wheelFlush.Start();
        }

        private void SendWheel(int at)
        {
            int dy = wheelDeltaY, dx = wheelDeltaX;
            wheelDeltaY = 0;
            wheelDeltaX = 0;
            if (dy == 0 && dx == 0) return;
            lastWheelAt = at;
            // CEF: +Y = up, +X = left.  CDP: +Y = down, +X = right.
            Cdp("wheel", "Input.dispatchMouseEvent",
                Mouse("mouseWheel", wheelX, wheelY, ",\"deltaX\":" + (-dx).ToString(CultureInfo.InvariantCulture) + ",\"deltaY\":" + (-dy).ToString(CultureInfo.InvariantCulture)));
        }

        public void TypeText(string text)
        {
            if (core == null || string.IsNullOrEmpty(text)) return;
            var sb = new StringBuilder();
            text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            foreach (char c in text)
            {
                if (c == '\n')
                {
                    FlushText(sb);
                    PressKey(13);
                }
                else sb.Append(c);
            }
            FlushText(sb);
        }

        private void FlushText(StringBuilder sb)
        {
            if (sb.Length == 0) return;
            Cdp("insertText", "Input.insertText", "{\"text\":" + new JavaScriptSerializer().Serialize(sb.ToString()) + "}");
            sb.Length = 0;
        }

        public void PressKey(int vk)
        {
            if (core == null) return;
            string key, code;
            switch (vk)
            {
                case 8: key = "Backspace"; code = "Backspace"; break;
                case 9: key = "Tab"; code = "Tab"; break;
                case 13: key = "Enter"; code = "Enter"; break;
                case 27: key = "Escape"; code = "Escape"; break;
                case 37: key = "ArrowLeft"; code = "ArrowLeft"; break;
                case 38: key = "ArrowUp"; code = "ArrowUp"; break;
                case 39: key = "ArrowRight"; code = "ArrowRight"; break;
                case 40: key = "ArrowDown"; code = "ArrowDown"; break;
                case 46: key = "Delete"; code = "Delete"; break;
                case 32: key = " "; code = "Space"; break;
                default: key = "Unidentified"; code = ""; break;
            }
            string v = ",\"windowsVirtualKeyCode\":" + vk.ToString(CultureInfo.InvariantCulture) +
                       ",\"nativeVirtualKeyCode\":" + vk.ToString(CultureInfo.InvariantCulture) +
                       ",\"key\":\"" + key + "\",\"code\":\"" + code + "\"";
            Cdp("key", "Input.dispatchKeyEvent", "{\"type\":\"rawKeyDown\"" + v + "}");
            if (vk == 13) Cdp("key", "Input.dispatchKeyEvent", "{\"type\":\"char\",\"text\":\"\\r\"" + v + "}");
            Cdp("key", "Input.dispatchKeyEvent", "{\"type\":\"keyUp\"" + v + "}");
        }

        private void Cdp(string op, string method, string json)
        {
            if (core == null) return;
            try
            {
                core.CallDevToolsProtocolMethodAsync(method, json).ContinueWith(delegate(Task<string> t)
                {
                    if (t.IsFaulted && t.Exception != null) Fail(op, t.Exception.GetBaseException());
                });
            }
            catch (Exception ex) { Fail(op, ex); }
        }

        // ----- zoom / data -----

        public void SetZoomLevel(double level)
        {
            zoomLevel = level;
            ApplyZoom();
        }

        public void SetBaseScale(double scale)
        {
            if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale)) scale = 1;
            if (Math.Abs(scale - baseScale) < 0.001) return;
            baseScale = scale;
            ApplyZoom();
        }

        private void ApplyZoom()
        {
            if (core == null) return;
            try { web.ZoomFactor = Math.Max(0.25, Math.Min(5, baseScale * Math.Pow(1.2, zoomLevel))); }
            catch (Exception ex) { Fail("ZoomFactor", ex); }
        }

        public void ClearCookies()
        {
            if (core == null) return;
            try { core.CookieManager.DeleteAllCookies(); }
            catch (Exception ex) { Fail("ClearCookies", ex); }
        }

        // ----- lifetime -----

        /// <summary>Low memory target while hidden (WebView2 trims caches), normal when shown.</summary>
        public void SetIdle(bool idle)
        {
            idleRequested = idle;
            idleSet = true;
            if (core == null || disposed) return;   // applied from OnInitCompleted once the engine exists
            ApplyIdle();
        }

        private void ApplyIdle()
        {
            if (!idleSet || core == null || disposed) return;
            try { core.MemoryUsageTargetLevel = idleRequested ? CoreWebView2MemoryUsageTargetLevel.Low : CoreWebView2MemoryUsageTargetLevel.Normal; }
            catch (Exception ex) { Fail("SetIdle", ex); }
            // hidden: nothing may keep making sound (embedded players in iframes, audio elements); shown again: normal
            try { core.IsMuted = idleRequested; }
            catch (Exception ex) { Fail("SetIdle mute", ex); }
        }

        /// <summary>Frees the WebView2 (its renderer processes go away). Afterwards the view is an inert, not-ready stub.</summary>
        public void Dispose()
        {
            if (disposed) return;
            string url = Address;
            disposed = true;
            pendingUrl = url;
            if (themeHook != null) { Theme.Changed -= themeHook; themeHook = null; }
            wheelFlush.Stop();
            moveFlush.Stop();
            try
            {
                web.CoreWebView2InitializationCompleted -= OnInitCompleted;
                Content = null;
                core = null;
                var d = web as IDisposable;
                if (d != null) d.Dispose();
            }
            catch (Exception ex) { Fail("Dispose", ex); }
            lock (envLock)
            {
                if (--live <= 0)
                {
                    live = 0;
                    envTask = null;   // lets the browser process exit; the next view makes a new environment
                    GC.Collect();
                }
            }
        }

        // ----- store launcher links -----

        private static readonly string[] LauncherSchemes = { "steam", "com.epicgames.launcher", "goggalaxy", "uplay", "eadesktop", "origin2", "battlenet", "msxbox", "ms-windows-store" };

        private static bool LauncherLink(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return false;
            int colon = uri.IndexOf(':');
            if (colon <= 0) return false;
            string scheme = uri.Substring(0, colon);
            foreach (string s in LauncherSchemes) if (string.Equals(s, scheme, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private void OpenLauncher(string uri)
        {
            try
            {
                System.Diagnostics.Process.Start(uri);
                Raise(PageNotice, "Opening in the store app");
            }
            catch (Exception ex) { Raise(PageNotice, "That store app is not installed"); logger.Info("SeleneBrowser: launcher link: " + ex.Message); }
        }

        // ----- helpers -----

        private void Raise(Action<string> handler, string value)
        {
            Post(handler, value);
        }

        private void Post(Action<string> handler, string value)
        {
            if (handler == null) return;
            Dispatcher.BeginInvoke(new Action(delegate
            {
                try { handler(value); }
                catch (Exception ex) { logger.Error(ex, "SeleneBrowser: EdgeView handler failed"); }
            }));
        }

        private void Fail(string what, Exception ex)
        {
            if (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
            bool first;
            lock (logged) first = logged.Add(what);
            if (first) logger.Error(ex, "SeleneBrowser: EdgeView " + what + " failed");
        }
    }
}
