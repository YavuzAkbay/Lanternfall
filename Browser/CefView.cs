// CefView.cs - thin, reflection-only wrapper around ONE CefSharp.Wpf.ChromiumWebBrowser.
//
// SeleneBrowser.dll never references CefSharp at compile time (Playnite already loads it), so
// every CefSharp member is reached by reflection. Reusable by any surface (browser, YouTube player):
//
//   var v = new CefView();  host.Children.Add(v);       // WPF UserControl hosting the chromium element
//   v.LoadEnd += url => ...;                            // main frame finished loading (UI thread)
//   v.AddressChanged += url => ...;  v.TitleChanged += title => ...;
//   v.Navigate("https://example.com");
//   v.ExecuteScript("...");                             // fire and forget
//   v.EvaluateAsync("JSON.stringify({a:1})", r => ...); // r = string result or null; runs on the UI thread, never blocks
//   v.Click(x, y); v.MoveMouse(x, y); v.Wheel(x, y, dy); // TRUSTED input (real user gestures), coordinates in DIPs of this view
//   v.TypeText("abc"); v.PressKey(13);                  // trusted key events (char events / virtual key codes)
//   v.SetZoomLevel(0.5);                                // Chromium zoom level, page scale = 1.2 ^ level
//
// Failures never throw: they are logged once per operation ("SeleneBrowser: CefView ...") and the call is a no-op.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ex = System.Linq.Expressions.Expression;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;

namespace SeleneBrowser
{
    public class CefView : UserControl, IPageView
    {
        public UIElement Element { get { return this; } }
        public bool IsFullScreen { get { return false; } }
        public event EventHandler FullScreenChanged { add { } remove { } }

        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly HashSet<string> logged = new HashSet<string>();
        private readonly Dictionary<string, MethodInfo> extCache = new Dictionary<string, MethodInfo>();
        private object chromium;
        private Type chromiumType;

        /// <summary>Main frame finished loading; argument is its URL. Raised on the UI thread.</summary>
        public event Action<string> LoadEnd;
        public event Action<string> AddressChanged;
        public event Action<string> TitleChanged;
        // CEF fallback: pop-ups stay in the same view (the Edge engine opens them as tabs)
        public event Action<string> NewWindowRequested { add { } remove { } }
        public event Action<string> PageNotice { add { } remove { } }

        public CefView()
        {
            Focusable = false;
            try
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "CefSharp.Wpf");
                if (asm == null) asm = Assembly.Load("CefSharp.Wpf");
                chromiumType = asm.GetType("CefSharp.Wpf.ChromiumWebBrowser", true);
                chromium = Activator.CreateInstance(chromiumType);
                Content = (UIElement)chromium;
                Subscribe("FrameLoadEnd", OnFrameLoadEnd);
                Subscribe("AddressChanged", delegate(object e) { Raise(AddressChanged, Prop(e, "NewValue") as string); });
                Subscribe("TitleChanged", delegate(object e) { Raise(TitleChanged, Prop(e, "NewValue") as string); });
            }
            catch (Exception ex)
            {
                logger.Error(ex, "SeleneBrowser: CefView creation failed");
                chromium = null;
                Content = Theme.Text("Browser unavailable: " + ex.Message, 24, Theme.Ink);
            }
        }

        // ----- state -----

        public bool IsReady { get { return chromium != null && Get<bool>("IsBrowserInitialized"); } }
        public string Address { get { return chromium == null ? "" : (Prop(chromium, "Address") as string ?? ""); } }
        public string Title { get { return chromium == null ? "" : (Prop(chromium, "Title") as string ?? ""); } }
        public bool CanGoBack { get { return chromium != null && Get<bool>("CanGoBack"); } }

        public void Navigate(string url)
        {
            if (chromium == null || string.IsNullOrEmpty(url)) return;
            try { chromiumType.GetProperty("Address").SetValue(chromium, url, null); }
            catch (Exception ex) { Fail("Navigate", ex); }
        }

        public void GoBack() { CallBrowser("GoBack"); }
        public void GoForward() { CallBrowser("GoForward"); }
        public void Reload() { CallBrowser("Reload"); }

        // ----- scripting -----

        public void ExecuteScript(string js)
        {
            if (!IsReady) return;
            try { CallExt("ExecuteScriptAsync", 2, js); }
            catch (Exception ex) { Fail("ExecuteScript", ex); }
        }

        /// <summary>Runs js (an expression) and calls onResult on the UI thread with its value as string (null on failure).</summary>
        public void EvaluateAsync(string js, Action<string> onResult)
        {
            if (!IsReady) { Post(onResult, null); return; }
            try
            {
                var task = CallExt("EvaluateScriptAsync", 2, js) as Task;
                if (task == null) { Post(onResult, null); return; }
                task.ContinueWith(delegate(Task t) { Post(onResult, ReadEvalResult(t)); });
            }
            catch (Exception ex) { Fail("EvaluateAsync", ex); Post(onResult, null); }
        }

        private string ReadEvalResult(Task t)
        {
            try
            {
                if (t.IsFaulted || t.IsCanceled) return null;
                var response = Prop(t, "Result");
                if (response == null || !(bool)Prop(response, "Success")) return null;
                var value = Prop(response, "Result");
                return value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
            }
            catch (Exception ex) { Fail("EvaluateAsync result", ex); return null; }
        }

        // ----- trusted input (CefSharp browser host) -----

        public void MoveMouse(double x, double y)
        {
            var host = Host();
            if (host == null) return;
            try
            {
                var m = FindMethod(host.GetType(), "SendMouseMoveEvent", "MouseEvent");
                Invoke(host, m, MakeMouse(m, x, y), false);
            }
            catch (Exception ex) { Fail("MouseMove", ex); }
        }

        /// <summary>Real left click (move, press, release) at view coordinates in DIPs.</summary>
        public void Click(double x, double y)
        {
            var host = Host();
            if (host == null) return;
            try
            {
                MoveMouse(x, y);
                var m = FindMethod(host.GetType(), "SendMouseClickEvent", "MouseEvent");
                var button = Enum.Parse(m.GetParameters()[1].ParameterType, "Left");
                Invoke(host, m, MakeMouse(m, x, y), button, false, 1);
                Invoke(host, m, MakeMouse(m, x, y), button, true, 1);
            }
            catch (Exception ex) { Fail("Click", ex); }
        }

        /// <summary>Mouse wheel at (x, y); deltaY &gt; 0 scrolls up, &lt; 0 scrolls down (pixels).</summary>
        public void Wheel(double x, double y, int deltaY) { Wheel(x, y, deltaY, 0); }

        /// <summary>Trusted wheel at (x,y): positive deltaY scrolls up, positive deltaX scrolls left.</summary>
        public void Wheel(double x, double y, int deltaY, int deltaX)
        {
            var host = Host();
            if (host == null) return;
            try
            {
                var m = FindMethod(host.GetType(), "SendMouseWheelEvent", "MouseEvent");
                Invoke(host, m, MakeMouse(m, x, y), deltaX, deltaY);
            }
            catch (Exception ex) { Fail("Wheel", ex); }
        }

        /// <summary>Types text into the focused editable element with char key events. '\n' sends Enter.</summary>
        public void TypeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (char c in text)
            {
                if (c == '\n' || c == '\r') PressKey(13);
                else SendKey("Char", c, true);
            }
        }

        /// <summary>Press and release a Windows virtual key (8 Backspace, 13 Enter, 27 Esc, 37..40 arrows, 46 Delete...).</summary>
        public void PressKey(int virtualKey)
        {
            SendKey("RawKeyDown", virtualKey, true);
            if (virtualKey == 13) SendKey("Char", 13, true);
            SendKey("KeyUp", virtualKey, true);
        }

        private void SendKey(string type, int code, bool editable)
        {
            var host = Host();
            if (host == null) return;
            try
            {
                var m = FindMethod(host.GetType(), "SendKeyEvent", "KeyEvent");
                var kt = m.GetParameters()[0].ParameterType;
                object ev = Activator.CreateInstance(kt);
                SetProp(ev, "Type", type);
                SetProp(ev, "WindowsKeyCode", code);
                SetProp(ev, "FocusOnEditableField", editable);
                SetProp(ev, "IsSystemKey", false);
                Invoke(host, m, ev);
            }
            catch (Exception ex) { Fail("SendKey", ex); }
        }

        private double userLevel, baseLevel;   // the page's zoom level = the screen's base scale (as a level) + the user's

        public void SetBaseScale(double scale)
        {
            if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale)) scale = 1;
            double l = Math.Log(scale) / Math.Log(1.2);
            if (Math.Abs(l - baseLevel) < 0.005) return;
            baseLevel = l;
            SetZoomLevel(userLevel);
        }

        public void SetZoomLevel(double level)
        {
            userLevel = level;
            var host = Host();
            if (host == null) return;
            try { Invoke(host, FindMethod(host.GetType(), "SetZoomLevel", null), level + baseLevel); }
            catch (Exception ex) { Fail("SetZoomLevel", ex); }
        }

        /// <summary>Deletes every cookie of the global cookie manager (fire and forget).</summary>
        public void ClearCookies()
        {
            try
            {
                var cef = FindCefType("CefSharp.Cef");
                var mgr = cef.GetMethod("GetGlobalCookieManager", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
                Invoke(mgr, FindMethod(mgr.GetType(), "DeleteCookiesAsync", null));
            }
            catch (Exception ex) { Fail("ClearCookies", ex); }
        }

        public void SetIdle(bool idle) { }

        /// <summary>Disposes the chromium element; later calls are no-ops.</summary>
        public void Dispose()
        {
            var c = chromium as IDisposable;
            chromium = null;
            Content = null;
            try { if (c != null) c.Dispose(); }
            catch (Exception ex) { Fail("Dispose", ex); }
        }

        // ----- reflection plumbing -----

        private object Host()
        {
            if (!IsReady) return null;
            try
            {
                var m = FindExt("GetBrowserHost", 1);
                object host;
                if (m != null) host = m.Invoke(null, new object[] { chromium });
                else
                {
                    var get = FindExt("GetBrowser", 1);
                    var b = get != null ? get.Invoke(null, new object[] { chromium })
                                        : FindMethod(chromiumType, "GetBrowser", null).Invoke(chromium, null);
                    host = b == null ? null : Invoke(b, FindMethod(b.GetType(), "GetHost", null));
                }
                if (host != null) Invoke(host, FindMethod(host.GetType(), "SetFocus", null), true);
                return host;
            }
            catch (Exception ex) { Fail("browser host", ex); return null; }
        }

        private void CallBrowser(string name)
        {
            if (!IsReady) return;
            try
            {
                // CefSharp 152: WebBrowserExtensions.GoBack/GoForward/Reload(browser); older builds: IBrowser via GetBrowser()
                var ext = FindExt(name, 1);
                if (ext != null) { Invoke(null, ext, new object[] { chromium }); return; }
                var get = FindExt("GetBrowser", 1);
                var b = get != null ? get.Invoke(null, new object[] { chromium })
                                    : FindMethod(chromiumType, "GetBrowser", null).Invoke(chromium, null);
                if (b == null) return;
                Invoke(b, FindMethod(b.GetType(), name, null));
            }
            catch (Exception ex) { Fail(name, ex); }
        }

        /// <summary>Calls the static WebBrowserExtensions method `name` with exactly n parameters (browser first).</summary>
        private object CallExt(string name, int n, params object[] rest)
        {
            var m = FindExt(name, n);
            if (m == null) throw new MissingMethodException("CefSharp extension " + name + " not found");
            var args = new object[rest.Length + 1];
            args[0] = chromium;
            Array.Copy(rest, 0, args, 1, rest.Length);
            return Invoke(null, m, args);
        }

        // Finds a public static method `name` taking the chromium browser as first parameter. n = wanted parameter count
        // (the optional-parameter overloads of EvaluateScriptAsync are matched by n=2 + defaults, see Invoke).
        private MethodInfo FindExt(string name, int n)
        {
            string key = name + "/" + n;
            MethodInfo found;
            if (extCache.TryGetValue(key, out found)) return found;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!asm.GetName().Name.StartsWith("CefSharp", StringComparison.Ordinal)) continue;
                Type[] types;
                try { types = asm.GetExportedTypes(); } catch { continue; }
                foreach (var t in types)
                {
                    if (!(t.IsAbstract && t.IsSealed)) continue; // static classes only
                    foreach (var mi in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        if (mi.Name != name || mi.ContainsGenericParameters) continue;   // (EvaluateScriptAsync<T> can't be late-bound)
                        var ps = mi.GetParameters();
                        if (ps.Length < n || !ps[0].ParameterType.IsInstanceOfType(chromium)) continue;
                        if (n >= 2 && ps[1].ParameterType != typeof(string)) continue;
                        bool restOptional = true;
                        for (int i = n; i < ps.Length; i++) if (!ps[i].IsOptional) restOptional = false;
                        if (!restOptional) continue;
                        if (found == null || ps.Length < found.GetParameters().Length) found = mi;
                    }
                }
            }
            extCache[key] = found;
            return found;
        }

        // Instance method by name (class and interfaces); firstParam = simple type name of the first parameter, or null
        // to take the overload with the fewest parameters.
        private static MethodInfo FindMethod(Type type, string name, string firstParam)
        {
            var all = new List<MethodInfo>(type.GetMethods());
            foreach (var itf in type.GetInterfaces()) all.AddRange(itf.GetMethods());
            MethodInfo best = null;
            foreach (var m in all)
            {
                if (m.Name != name) continue;
                var ps = m.GetParameters();
                if (firstParam != null && (ps.Length == 0 || ps[0].ParameterType.Name != firstParam)) continue;
                if (best == null || ps.Length < best.GetParameters().Length) best = m;
            }
            if (best == null) throw new MissingMethodException(type.Name + "." + name);
            return best;
        }

        // Invokes m, padding missing trailing parameters with their defaults (null / false / 5 s for TimeSpan?).
        private static object Invoke(object target, MethodInfo m, params object[] given)
        {
            var ps = m.GetParameters();
            var args = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                if (i < given.Length) { args[i] = given[i]; continue; }
                var pt = ps[i].ParameterType;
                if (Nullable.GetUnderlyingType(pt) == typeof(TimeSpan)) args[i] = TimeSpan.FromSeconds(5);
                else if (ps[i].HasDefaultValue && !(ps[i].DefaultValue is DBNull) && ps[i].DefaultValue != Type.Missing) args[i] = ps[i].DefaultValue;
                else args[i] = pt.IsValueType ? Activator.CreateInstance(pt) : null;
            }
            return m.Invoke(target, args);
        }

        // MouseEvent(x, y, modifiers) built from the method's first parameter type; falls back to property setters.
        private static object MakeMouse(MethodInfo m, double x, double y)
        {
            var t = m.GetParameters()[0].ParameterType;
            foreach (var c in t.GetConstructors())
            {
                var ps = c.GetParameters();
                if (ps.Length == 3 && ps[0].ParameterType == typeof(int) && ps[1].ParameterType == typeof(int))
                    return c.Invoke(new object[] { (int)x, (int)y, Activator.CreateInstance(ps[2].ParameterType) });
            }
            var o = Activator.CreateInstance(t);
            SetProp(o, "X", (int)x);
            SetProp(o, "Y", (int)y);
            return o;
        }

        private static void SetProp(object o, string name, object value)
        {
            var p = o.GetType().GetProperty(name);
            if (p == null || !p.CanWrite) throw new MissingMemberException(o.GetType().Name, name);
            object v = value;
            if (p.PropertyType.IsEnum) v = value is string ? Enum.Parse(p.PropertyType, (string)value) : Enum.ToObject(p.PropertyType, value);
            p.SetValue(o, v, null); // o is a boxed struct: the box is mutated in place
        }

        private static object Prop(object o, string name)
        {
            if (o == null) return null;
            var p = o.GetType().GetProperty(name);
            return p == null ? null : p.GetValue(o, null);
        }

        private T Get<T>(string prop)
        {
            try { var v = Prop(chromium, prop); return v is T ? (T)v : default(T); }
            catch { return default(T); }
        }

        private static Type FindCefType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!asm.GetName().Name.StartsWith("CefSharp", StringComparison.Ordinal)) continue;
                var t = asm.GetType(fullName);
                if (t != null) return t;
            }
            throw new TypeLoadException(fullName);
        }

        // Subscribes cb(eventArgs) to a chromium event of any delegate type, via a compiled lambda of the right shape.
        private void Subscribe(string eventName, Action<object> cb)
        {
            try
            {
                var ev = chromiumType.GetEvent(eventName);
                var inv = ev.EventHandlerType.GetMethod("Invoke").GetParameters();
                var sender = Ex.Parameter(inv[0].ParameterType, "s");
                var args = Ex.Parameter(inv[1].ParameterType, "e");
                var body = Ex.Invoke(Ex.Constant(cb), Ex.Convert(args, typeof(object)));
                ev.AddEventHandler(chromium, Ex.Lambda(ev.EventHandlerType, body, sender, args).Compile());
            }
            catch (Exception ex) { Fail("subscribe " + eventName, ex); }
        }

        private void OnFrameLoadEnd(object e)
        {
            try
            {
                var frame = Prop(e, "Frame");
                if (frame == null || !(bool)Prop(frame, "IsMain")) return;
                Raise(LoadEnd, Prop(e, "Url") as string);
            }
            catch (Exception ex) { Fail("FrameLoadEnd", ex); }
        }

        private void Raise(Action<string> handler, string value)
        {
            if (handler == null) return;
            Post(handler, value);
        }

        private void Post(Action<string> handler, string value)
        {
            if (handler == null) return;
            Dispatcher.BeginInvoke(new Action(delegate
            {
                try { handler(value); }
                catch (Exception ex) { logger.Error(ex, "SeleneBrowser: CefView handler failed"); }
            }));
        }

        private void Fail(string what, Exception ex)
        {
            var inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
            if (logged.Add(what)) logger.Error(inner, "SeleneBrowser: CefView " + what + " failed");
        }
    }
}
