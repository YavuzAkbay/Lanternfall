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
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Playnite.SDK;

namespace SeleneBrowser
{
    public class EdgeView : UserControl, IPageView
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly object envLock = new object();
        private static Task<CoreWebView2Environment> envTask;
        private static bool loaderPathSet;

        private readonly HashSet<string> logged = new HashSet<string>();
        private readonly WebView2CompositionControl web = new WebView2CompositionControl();
        private CoreWebView2 core;
        private string pendingUrl;
        private double zoomLevel;
        private bool initFailed;

        /// <summary>Raised once when WebView2 could not start; the host switches to another engine.</summary>
        public event Action InitFailed;

        private void SetFailed()
        {
            if (initFailed) return;
            initFailed = true;
            var h = InitFailed;
            if (h != null) h();
        }

        public event Action<string> LoadEnd;
        public event Action<string> AddressChanged;
        public event Action<string> TitleChanged;
        public event EventHandler FullScreenChanged;

        public UIElement Element { get { return this; } }

        // ----- availability / environment -----

        private static void EnsureLoaderPath()
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
                if (envTask == null)
                {
                    EnsureLoaderPath();
                    var options = new CoreWebView2EnvironmentOptions();
                    options.AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required";
                    envTask = CoreWebView2Environment.CreateAsync(null, Path.Combine(SeleneBrowserPlugin.DataDir, "webview2"), options);
                }
                return envTask;
            }
        }

        // ----- creation -----

        public EdgeView()
        {
            Focusable = false;
            Content = web;
            try
            {
                // the theme's backdrop is a gradient: a page loading shows its scrim color, not white
                var bg = Theme.Background as SolidColorBrush;
                System.Windows.Media.Color c = bg != null ? bg.Color : Theme.ScrimColor;
                web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, c.R, c.G, c.B);
            }
            catch (Exception ex) { Fail("background", ex); }
            web.CoreWebView2InitializationCompleted += OnInitCompleted;
            try
            {
                var ui = TaskScheduler.FromCurrentSynchronizationContext();
                GetEnvironment().ContinueWith(delegate(Task<CoreWebView2Environment> t)
                {
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

        private void OnInitCompleted(object sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            if (!e.IsSuccess) { SetFailed(); Fail("init", e.InitializationException); return; }
            try
            {
                core = web.CoreWebView2;
                var s = core.Settings;
                s.AreDefaultContextMenusEnabled = false;
                s.IsStatusBarEnabled = false;
                s.IsZoomControlEnabled = false;
                s.AreBrowserAcceleratorKeysEnabled = false;
                core.NewWindowRequested += delegate(object o, CoreWebView2NewWindowRequestedEventArgs a)
                {
                    a.Handled = true;
                    if (!string.IsNullOrEmpty(a.Uri)) Navigate(a.Uri);
                };
                core.DownloadStarting += delegate(object o, CoreWebView2DownloadStartingEventArgs a) { a.Cancel = true; };
                core.PermissionRequested += delegate(object o, CoreWebView2PermissionRequestedEventArgs a)
                {
                    a.State = CoreWebView2PermissionState.Deny;
                };
                core.NavigationCompleted += delegate { Raise(LoadEnd, core.Source); };
                core.SourceChanged += delegate { Raise(AddressChanged, core.Source); };
                core.DocumentTitleChanged += delegate { Raise(TitleChanged, core.DocumentTitle); };
                core.ContainsFullScreenElementChanged += delegate { RaiseFullScreen(); };
                // element fullscreen is emulated in the page (see EdgeFullscreen.cs), reported back as a web message
                core.AddScriptToExecuteOnDocumentCreatedAsync(EdgeFullscreen.Script);
                core.WebMessageReceived += delegate(object o, CoreWebView2WebMessageReceivedEventArgs a)
                {
                    string m;
                    try { m = a.TryGetWebMessageAsString(); } catch (Exception) { return; }
                    if (m == null || !m.StartsWith(EdgeFullscreen.Message)) return;
                    bool on = m.EndsWith("1");
                    if (on != pageFullScreen) { pageFullScreen = on; RaiseFullScreen(); }
                };
                core.NavigationStarting += delegate { if (pageFullScreen) { pageFullScreen = false; RaiseFullScreen(); } };
                if (zoomLevel != 0) web.ZoomFactor = Math.Pow(1.2, zoomLevel);
                if (!string.IsNullOrEmpty(pendingUrl)) { string u = pendingUrl; pendingUrl = null; core.Navigate(u); }
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

        private double Z { get { double z = 1; try { z = web.ZoomFactor; } catch (Exception) { } return z <= 0 ? 1 : z; } }

        private static string N(double v) { return v.ToString("0.##", CultureInfo.InvariantCulture); }

        private string Mouse(string type, double x, double y, string extra)
        {
            double z = Z;
            return "{\"type\":\"" + type + "\",\"x\":" + N(x / z) + ",\"y\":" + N(y / z) + (extra ?? "") + "}";
        }

        public void MoveMouse(double x, double y)
        {
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
            // CEF: +Y = up, +X = left.  CDP: +Y = down, +X = right.
            Cdp("wheel", "Input.dispatchMouseEvent",
                Mouse("mouseWheel", x, y, ",\"deltaX\":" + (-deltaX).ToString(CultureInfo.InvariantCulture) + ",\"deltaY\":" + (-deltaY).ToString(CultureInfo.InvariantCulture)));
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
            if (core == null) return;
            try { web.ZoomFactor = Math.Pow(1.2, level); }
            catch (Exception ex) { Fail("SetZoomLevel", ex); }
        }

        public void ClearCookies()
        {
            if (core == null) return;
            try { core.CookieManager.DeleteAllCookies(); }
            catch (Exception ex) { Fail("ClearCookies", ex); }
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
