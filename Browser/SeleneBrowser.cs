// Nomad: a Playnite generic plugin exposing a controller-first CefSharp browser to a fullscreen theme.
// BrowserControl follows the pad contract of docs\NOMAD-SPEC.md (HandlePad / OnShown / OnHidden / Hints).
//   Focus mode : D-pad spatial navigation (JS shim draws a highlight box), A = trusted click.
//   Cursor mode: right stick moves a WPF cursor, A clicks, LT/RT scroll the wheel.
//   Top bar    : Back / Forward / Reload / Address / Home / Zoom.  Text fields use the on-screen keyboard.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Playnite.SDK;
using Playnite.SDK.Controls;
using Playnite.SDK.Plugins;

namespace SeleneBrowser
{
    /// <summary>Bookmark or history record persisted as JSON in the plugin data folder.</summary>
    public class BrowserEntry
    {
        public string Title { get; set; }
        public string Url { get; set; }
        public long Time { get; set; }
    }

    public partial class BrowserControl : PluginUserControl, INotifyPropertyChanged
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private enum Mode { Focus, Cursor, Bar, Menu }

        private sealed class MenuEntry
        {
            public string Label;
            public Func<bool> Run; // false = close the overlay
            public Border Box;
        }

        private const string SearchAction = "https://www.google.com/search";
        private const string SearchUrl = SearchAction + "?q=";
        private const double ZoomStep = 0.5;
        private static readonly object fileLock = new object();

        private IPageView view = CreateView();
        private Grid topBar;
        private readonly OnScreenKeyboard keyboard = new OnScreenKeyboard();
        private readonly PadInput pad = new PadInput();
        private readonly RepeatLimiter limiter = new RepeatLimiter();
        private readonly DispatcherTimer poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly string shim;
        private readonly Grid page = new Grid();
        private System.Windows.Shapes.Polygon cursorSprite;
        private readonly Canvas cursorLayer =new Canvas { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        private readonly Border toast = new Border();
        private readonly TextBlock toastText = Theme.Text("", 22, Theme.Ink);
        private readonly Border menuBox = new Border();
        private readonly Grid menuShade = new Grid();
        private readonly StackPanel menuList = new StackPanel();
        private readonly HintBar hintText = new HintBar(18, HorizontalAlignment.Right);
        private readonly TextBlock addressText = Theme.Text("", 22, Theme.Ink);
        private readonly List<Border> barItems = new List<Border>();
        private readonly List<Action> barActions = new List<Action>();
        private readonly List<MenuEntry> menuItems = new List<MenuEntry>();
        private List<BrowserEntry> bookmarks;
        private List<BrowserEntry> history;

        private Mode mode = Mode.Focus;
        private bool shown;
        private bool viewDisposed;
        private string resumeUrl;
        private readonly DispatcherTimer disposeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        private int barIndex;
        private int menuIndex;
        private double zoom;
        private Point cursor;
        private double held;
        private long lastTick, lastMove;
        private string hints = "";

        public event PropertyChangedEventHandler PropertyChanged;
        public string Hints { get { return hints; } }

        private static string BookmarksFile { get { return Path.Combine(SeleneBrowserPlugin.DataDir, "bookmarks.json"); } }
        private static string HistoryFile { get { return Path.Combine(SeleneBrowserPlugin.DataDir, "history.json"); } }
        private double Scale { get { return Math.Pow(1.2, zoom); } } // css px -> DIP
        // the start page is a data: URL; WebView2 reports such pages with an empty Source after history navigation
        private bool IsHome
        {
            get
            {
                string a = view.Address ?? "";
                return a.Length == 0 || a.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || a == "about:blank";
            }
        }

        private static IPageView CreateView() { return PageViews.Create(); }

        private void OnFullScreenChanged(object sender, EventArgs e)
        {
            bool fs = view.IsFullScreen;
            WakePointer();   // (shown again on leaving fullscreen; the idle timer starts over on entering it)
            ApplyBars();   // top bar / hints: hidden in fullscreen or while the auto-hide has stepped aside
            if (fs && mode == Mode.Bar) EnterFocus(false);
            // left fullscreen: put the highlight back once the player has shrunk to its place in the page
            if (!fs && mode == Mode.Focus) { After(250, delegate { Script("show()"); }); After(700, delegate { Script("show()"); }); }
        }

        private void HookView()
        {
            view.LoadEnd += OnLoadEnd;
            view.AddressChanged += OnViewAddressChanged;
            view.TitleChanged += OnTitleChanged;
            view.FullScreenChanged += OnFullScreenChanged;
            var edge = view as EdgeView;
            if (edge != null) edge.InitFailed += OnEdgeInitFailed;
        }

        private void OnViewAddressChanged(string url) { UpdateAddress(); BarsNewPage(url); }

        // WebView2 was installed but would not start (broken runtime, policy...): carry on with the CEF engine
        private void OnEdgeInitFailed()
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                try
                {
                    logger.Warn("SeleneBrowser: WebView2 failed to start - switching to CEF");
                    view.LoadEnd -= OnLoadEnd;
                    view.AddressChanged -= OnViewAddressChanged;
                    view.TitleChanged -= OnTitleChanged;
                    view.FullScreenChanged -= OnFullScreenChanged;
                    int at = page.Children.IndexOf(view.Element);
                    page.Children.Remove(view.Element);
                    view = new CefView();
                    page.Children.Insert(Math.Max(0, at), view.Element);
                    HookView();
                    GoHome();
                }
                catch (Exception ex) { logger.Error(ex, "SeleneBrowser: CEF fallback"); }
            }));
        }
        public BrowserControl()
        {
            shim = ShimSource.Replace("%ACCENT%", Theme.AccentHex);
            bookmarks = LoadList(BookmarksFile);
            if (bookmarks.Count == 0) bookmarks = DefaultBookmarks();
            history = LoadList(HistoryFile);
            Focusable = false;
            Content = BuildLayout();
            InitExtras();

            HookView();
            poll.Tick += OnPollTick;
            // closed for good (hidden / unloaded for a while): free the page engine; Loaded / OnShown bring it back
            disposeTimer.Tick += delegate { DisposeView(); };
            Unloaded += delegate { Park(); };
            Loaded += delegate
            {
                disposeTimer.Stop();
                EnsureView();
                if (!shown) disposeTimer.Start();   // re-attached but not shown: still free the engine soon
            };
            GoHome();
            UpdateHints();
        }

        // ----- pad contract -----

        public bool HandlePad(string button)
        {
            try
            {
                WakePointer();   // any press brings a faded cursor / highlight back
                if (keyboard.IsOpen)
                {
                    keyboard.HandlePad(button);
                    if (!keyboard.IsOpen) UpdateHints();   // Done / Cancel: back to the page's hints
                    return true;   // B on the keyboard cancels it, never closes the browser
                }
                if (mode == Mode.Menu) return settings.IsOpen ? HandleSettings(button) : HandleMenu(button);
                if (mode == Mode.Bar) return HandleBar(button);
                return HandlePage(button);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "SeleneBrowser: HandlePad " + button);
                return true;
            }
        }

        public void OnShown()
        {
            disposeTimer.Stop();
            EnsureView();
            view.SetIdle(false);
            Presence.Enter("browser");
            shown = true;
            lastTick = clock.ElapsedMilliseconds;
            poll.Start();
            BarsStart();
            InjectShim();
            UpdateHints();
        }

        public void OnHidden() { Park(); }

        // hidden or unloaded: pause media, trim memory, and drop the engine if nobody comes back soon
        private void Park()
        {
            shown = false;
            BarsStop();
            poll.Stop();
            Presence.Leave("browser");
            if (!viewDisposed)
            {
                // leave element fullscreen (top bar / hints must be back when the browser reopens)
                view.ExecuteScript("window.__nomadExitFs&&window.__nomadExitFs()");
                view.ExecuteScript("document.querySelectorAll('video,audio').forEach(function(m){try{m.pause()}catch(e){}})");
                view.SetIdle(true);
            }
            disposeTimer.Stop();
            disposeTimer.Start();
        }

        private void DisposeView()
        {
            disposeTimer.Stop();
            if (shown || viewDisposed) return;
            try
            {
                string a = IsHome ? null : view.Address;
                resumeUrl = string.IsNullOrEmpty(a) ? null : a;   // the page the user left is restored on return
                view.LoadEnd -= OnLoadEnd;
                view.AddressChanged -= OnViewAddressChanged;
                view.TitleChanged -= OnTitleChanged;
                view.FullScreenChanged -= OnFullScreenChanged;
                var oldEdge = view as EdgeView;
                if (oldEdge != null) oldEdge.InitFailed -= OnEdgeInitFailed;
                page.Children.Remove(view.Element);
                view.Dispose();
                viewDisposed = true;
            }
            catch (Exception ex) { logger.Error(ex, "SeleneBrowser: dispose view"); }
        }

        // a fresh page engine after DisposeView
        private void EnsureView()
        {
            if (!viewDisposed) return;
            try
            {
                view = CreateView();
                viewDisposed = false;
                page.Children.Insert(0, view.Element);
                HookView();
                string u = resumeUrl;
                resumeUrl = null;
                if (u != null) view.Navigate(u); else GoHome();
                OnFullScreenChanged(null, EventArgs.Empty);   // top bar / hints visible again
            }
            catch (Exception ex) { logger.Error(ex, "SeleneBrowser: recreate view"); }
        }

        public void Navigate(string url)
        {
            var target = ResolveInput(url);
            if (target != null) view.Navigate(target);
        }

        // ----- page modes -----

        private bool HandlePage(string b)
        {
            switch (b)
            {
                case "DPadUp": case "DPadDown": case "DPadLeft": case "DPadRight":
                    if (!limiter.Allow(b)) return true;
                    if (mode == Mode.Cursor) EnterFocus(true);
                    else Script("move('" + b.Substring(4).ToLowerInvariant() + "')");
                    return true;
                case "A": if (limiter.IsFirst(b)) Activate(); return true;
                case "B": return !limiter.IsFirst(b) || BackOrClose();
                case "X": if (limiter.IsFirst(b)) view.Reload(); return true;
                case "Y": case "Back": if (limiter.IsFirst(b)) EnterBar(); return true;
                case "LeftShoulder": if (limiter.IsFirst(b)) view.GoBack(); return true;
                case "RightShoulder": if (limiter.IsFirst(b)) view.GoForward(); return true;
                case "TriggerLeft": if (limiter.Allow(b)) ScrollPage(-1); return true;
                case "TriggerRight": if (limiter.Allow(b)) ScrollPage(1); return true;
                case "LeftStick": if (limiter.Allow(b)) ZoomBy(-ZoomStep); return true;
                case "RightStick": if (limiter.Allow(b)) ZoomBy(ZoomStep); return true;
                case "Start": if (limiter.IsFirst(b)) OpenMenu(); return true;
            }
            return true;
        }

        // History back; on the start page with nothing to go back to, ask the host to close the overlay.
        private bool BackOrClose()
        {
            // (the emulated fullscreen may live in a player iframe: __nomadExitFs passes the exit down to every frame)
            if (view.IsFullScreen) { view.ExecuteScript("window.__nomadExitFs ? window.__nomadExitFs() : document.exitFullscreen()"); return true; }
            // the start page is the browser's top level: B there always closes (each trip home is a history entry,
            // so going "back" from it only reached another identical start page and B seemed to do nothing)
            if (IsHome) return false;
            if (view.CanGoBack) { view.GoBack(); return true; }
            GoHome();
            return true;
        }

        private void Script(string call)
        {
            view.ExecuteScript("window.__nomadNav&&window.__nomadNav." + call + ";");
        }

        private void InjectShim()
        {
            if (!view.IsReady) return;
            view.ExecuteScript(shim);
            if (mode == Mode.Focus) After(300, delegate { Script("first()"); });
        }

        private void OnLoadEnd(string url)
        {
            if (zoom != 0) view.SetZoomLevel(zoom);
            if (shown) InjectShim();
            if (url != null && url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) AddHistory(url, view.Title);
            UpdateAddress();
        }

        // A: trusted click on the highlighted element (focus mode) or at the cursor; text fields then open the keyboard.
        private void Activate()
        {
            if (mode == Mode.Cursor) { ClickAt(cursor.X, cursor.Y); return; }
            view.EvaluateAsync("window.__nomadNav?window.__nomadNav.info():null", delegate(string json)
            {
                double x, y;
                if (ParsePoint(json, out x, out y)) ClickAt(x * Scale, y * Scale);
            });
        }

        private void ClickAt(double x, double y)
        {
            view.Click(x, y);
            After(200, delegate
            {
                view.EvaluateAsync("window.__nomadNav?window.__nomadNav.editable():null", OpenFieldKeyboard);
            });
        }

        private void OpenFieldKeyboard(string json)
        {
            var d = ParseJson(json);
            if (d == null) return;
            bool multi = d.ContainsKey("multi") && (bool)d["multi"];
            bool pw = d.ContainsKey("password") && (bool)d["password"];
            string label = d.ContainsKey("label") ? d["label"] as string : "";
            string title = pw ? "Password" : (string.IsNullOrEmpty(label) ? "Type text" : label);
            keyboard.Open(title, d.ContainsKey("value") ? d["value"] as string : "", delegate(string text)
            {
                // select the field's content, replace it with trusted key events, Enter submits single-line fields
                view.EvaluateAsync("window.__nomadNav.selectAll()", delegate
                {
                    if (text.Length == 0) view.PressKey(8); else view.TypeText(text);
                    if (!multi) view.PressKey(13);
                });
            }, null, null);
            UpdateHints();
        }

        private void ScrollPage(int dir)
        {
            BarsScrolled(dir, 240);
            double wheel = -dir * 240;
            if (mode == Mode.Cursor) { view.Wheel(cursor.X, cursor.Y, (int)wheel); return; }
            view.EvaluateAsync("window.__nomadNav?window.__nomadNav.info():null", delegate(string json)
            {
                double x, y;
                if (ParsePoint(json, out x, out y)) view.Wheel(x * Scale, y * Scale, (int)wheel);
                else view.Wheel(page.ActualWidth / 2, page.ActualHeight / 2, (int)wheel);
            });
        }

        private void ZoomBy(double delta)
        {
            zoom = Math.Max(-3, Math.Min(5, zoom + delta));
            if (Math.Abs(zoom) < 0.01) zoom = 0;
            view.SetZoomLevel(zoom);
            Toast("Zoom " + Math.Round(Scale * 100).ToString(CultureInfo.InvariantCulture) + "%");
        }

        // ----- cursor mode (right stick) -----

        private void OnPollTick(object sender, EventArgs e)
        {
            if (!shown || keyboard.IsOpen || mode == Mode.Menu || mode == Mode.Bar) return;
            var w = Window.GetWindow(this);
            if (w == null || !w.IsActive) return;
            long now = clock.ElapsedMilliseconds;
            double dt = Math.Min(0.05, (now - lastTick) / 1000.0);
            lastTick = now;
            if (!pad.Update()) { HideWhenIdle(now); return; }
            double mag = Math.Sqrt(pad.RightX * pad.RightX + pad.RightY * pad.RightY);
            if (mag <= 0) { held = 0; HideWhenIdle(now); return; }
            WakePointer();
            if (mode == Mode.Focus) EnterCursor();
            held += dt;
            BarsMoved(dt);
            double speed = 1100 * mag * mag * (1 + Math.Min(held, 1.2) * 1.5) * dt;
            cursor = new Point(
                Math.Max(0, Math.Min(page.ActualWidth - 1, cursor.X + pad.RightX * speed)),
                Math.Max(0, Math.Min(page.ActualHeight - 1, cursor.Y - pad.RightY * speed)));
            Canvas.SetLeft(cursorSprite, cursor.X);
            Canvas.SetTop(cursorSprite, cursor.Y);
            if (now - lastMove >= 30) { lastMove = now; view.MoveMouse(cursor.X, cursor.Y); }
            EdgeScroll(now);
        }

        // A fullscreen video: the cursor (and the D-pad highlight) fade out after a moment without input, like a TV player.
        // Moving the stick or pressing a button brings them back.
        private const long PointerIdleMs = 1500;
        private long lastInputAt;
        private bool pointerHidden;

        private void WakePointer()
        {
            lastInputAt = clock.ElapsedMilliseconds;
            if (!pointerHidden) return;
            pointerHidden = false;
            cursorSprite.Visibility = Visibility.Visible;
            if (mode == Mode.Focus) Script("show()");
        }

        private void HideWhenIdle(long now)
        {
            if (pointerHidden || !view.IsFullScreen || keyboard.IsOpen || mode == Mode.Menu || mode == Mode.Bar) return;
            if (now - lastInputAt < PointerIdleMs) return;
            pointerHidden = true;
            cursorSprite.Visibility = Visibility.Hidden;
            if (mode == Mode.Focus) Script("hide()");
        }

        // the cursor pushed against an edge scrolls the page under it (like a console browser), faster the harder it pushes
        private const double EdgeZone = 36;
        private long lastEdge;
        private void EdgeScroll(long now)
        {
            if (now - lastEdge < 40) return;
            int dy = 0, dx = 0;
            if (cursor.Y >= page.ActualHeight - EdgeZone && pad.RightY < -0.25) dy = -1;        // stick down: scroll down
            else if (cursor.Y <= EdgeZone && pad.RightY > 0.25) dy = 1;
            if (cursor.X >= page.ActualWidth - EdgeZone && pad.RightX > 0.25) dx = -1;
            else if (cursor.X <= EdgeZone && pad.RightX < -0.25) dx = 1;
            if (dy == 0 && dx == 0) return;
            lastEdge = now;
            double push = Math.Max(Math.Abs(pad.RightX), Math.Abs(pad.RightY));
            int step = (int)(40 + 160 * push * push);   // 40..200 wheel units every 40 ms
            if (dy != 0) BarsScrolled(-dy, step);
            // the wheel goes a little inside the page, in the cursor's column/row: right at the edge it lands on a fixed header
            // or bottom bar, and on sites that scroll an inner panel (kick.com) a wheel there scrolls nothing
            double wx = cursor.X, wy = cursor.Y;
            if (dy != 0) wy = page.ActualHeight * (dy > 0 ? 0.3 : 0.7);
            if (dx != 0) wx = page.ActualWidth * (dx > 0 ? 0.3 : 0.7);
            view.Wheel(wx, wy, dy * step, dx * step);
        }

        private void EnterCursor()
        {
            mode = Mode.Cursor;
            if (cursor.X == 0 && cursor.Y == 0) cursor = new Point(page.ActualWidth / 2, page.ActualHeight / 2);
            // start the cursor where the highlight was
            view.EvaluateAsync("window.__nomadNav?window.__nomadNav.info():null", delegate(string json)
            {
                double x, y;
                if (mode == Mode.Cursor && ParsePoint(json, out x, out y)) cursor = new Point(x * Scale, y * Scale);
                Canvas.SetLeft(cursorSprite, cursor.X);
                Canvas.SetTop(cursorSprite, cursor.Y);
            });
            Script("hide()");
            cursorLayer.Visibility = Visibility.Visible;
            held = 0;
            UpdateHints();
        }

        private void EnterFocus(bool nearCursor)
        {
            mode = Mode.Focus;
            cursorLayer.Visibility = Visibility.Collapsed;
            if (nearCursor) Script("focusNear(" + (cursor.X / Scale).ToString("0.#", CultureInfo.InvariantCulture) + "," + (cursor.Y / Scale).ToString("0.#", CultureInfo.InvariantCulture) + ")");
            else Script("first()");
            UpdateBar();
            UpdateHints();
        }

        // ----- top bar -----

        private bool HandleBar(string b)
        {
            switch (b)
            {
                case "DPadLeft": if (limiter.Allow(b)) MoveBar(-1); break;
                case "DPadRight": if (limiter.Allow(b)) MoveBar(1); break;
                case "A": if (limiter.IsFirst(b)) barActions[barIndex](); break;
                case "DPadDown": case "B": case "Y": case "Back":
                    if (limiter.IsFirst(b)) EnterFocus(false);
                    break;
                case "Start": if (limiter.IsFirst(b)) OpenMenu(); break;
            }
            return true;
        }

        private void EnterBar()
        {
            if (view.IsFullScreen) return;   // the bar is hidden in fullscreen (B leaves fullscreen first)
            BarsShow();
            mode = Mode.Bar;
            cursorLayer.Visibility = Visibility.Collapsed;
            Script("hide()");
            UpdateBar();
            UpdateHints();
        }

        private void MoveBar(int d)
        {
            barIndex = (barIndex + d + barItems.Count) % barItems.Count;
            UpdateBar();
        }

        private void UpdateBar()
        {
            for (int i = 0; i < barItems.Count; i++) Theme.SetFocused(barItems[i], mode == Mode.Bar && i == barIndex);
        }

        private void UpdateAddress()
        {
            addressText.Text = IsHome || view.Address.Length == 0 ? "Search or enter address" : view.Address;
        }

        private void EditAddress()
        {
            BarsShow();
            string initial = IsHome ? "" : view.Address;
            Func<string, IEnumerable<string>> suggest = delegate(string t)
            {
                if (string.IsNullOrWhiteSpace(t)) return history.Take(5).Select(h => h.Url);
                return bookmarks.Concat(history)
                    .Where(h => (h.Url ?? "").IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0 || (h.Title ?? "").IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(h => h.Url).Distinct();
            };
            keyboard.Open("Address or search", initial, delegate(string text)
            {
                Navigate(text);
                EnterFocus(false);
            }, null, suggest);
            UpdateHints();
        }

        // ----- menu (Start) -----

        private void OpenMenu()
        {
            BarsShow();
            var bm = FindBookmark(view.Address);
            menuItems.Clear();
            if (!IsHome && view.Address.Length > 0)
                menuItems.Add(new MenuEntry
                {
                    Label = bm == null ? "Add bookmark" : "Remove bookmark",
                    Run = delegate { ToggleBookmark(); return true; }
                });
            menuItems.Add(new MenuEntry { Label = "Zoom in", Run = delegate { ZoomBy(ZoomStep); return true; } });
            menuItems.Add(new MenuEntry { Label = "Zoom out", Run = delegate { ZoomBy(-ZoomStep); return true; } });
            menuItems.Add(new MenuEntry { Label = "Reset zoom", Run = delegate { ZoomBy(-zoom); return true; } });
            menuItems.Add(new MenuEntry { Label = "Settings", Run = delegate { OpenSettings(); return true; } });
            menuItems.Add(new MenuEntry { Label = "Clear history and cookies", Run = delegate { ClearData(); return true; } });
            menuItems.Add(new MenuEntry { Label = "Close browser", Run = delegate { return false; } });
            menuList.Children.Clear();
            foreach (var m in menuItems)
            {
                m.Box = Theme.Tile(m.Label, 24, MenuIcon(m.Label), Look.Ghost);
                m.Box.Padding = new Thickness(24, 11, 28, 12);
                m.Box.Margin = new Thickness(0, 3, 0, 3);
                Theme.AlignLeft(m.Box);
                menuList.Children.Add(m.Box);
            }
            menuIndex = 0;
            mode = Mode.Menu;
            cursorLayer.Visibility = Visibility.Collapsed;
            menuShade.Visibility = Visibility.Visible;
            UpdateMenu();
            UpdateHints();
        }

        // Material icons for the menu's rows
        private static int MenuIcon(string label)
        {
            if (label.StartsWith("Add bookmark")) return 0xE867;      // bookmark_border
            if (label.StartsWith("Remove bookmark")) return 0xE866;   // bookmark
            if (label == "Zoom in") return 0xE8FF;
            if (label == "Zoom out") return 0xE900;
            if (label == "Reset zoom") return 0xE3BC;                 // crop_free (fit)
            if (label == "Settings") return 0xE8B8;                   // settings
            if (label.StartsWith("Clear")) return 0xE872;              // delete
            if (label.StartsWith("Close")) return 0xE5CD;              // close
            return 0;
        }

        private void CloseMenu()
        {
            menuShade.Visibility = Visibility.Collapsed;
            EnterFocus(false);
        }

        private bool HandleMenu(string b)
        {
            switch (b)
            {
                case "DPadUp": if (limiter.Allow(b)) { menuIndex = (menuIndex + menuItems.Count - 1) % menuItems.Count; UpdateMenu(); } break;
                case "DPadDown": if (limiter.Allow(b)) { menuIndex = (menuIndex + 1) % menuItems.Count; UpdateMenu(); } break;
                case "A":
                    if (!limiter.IsFirst(b)) break;
                    var item = menuItems[menuIndex];
                    CloseMenu();
                    return item.Run();
                case "B": case "Start": if (limiter.IsFirst(b)) CloseMenu(); break;
            }
            return true;
        }

        private void UpdateMenu()
        {
            for (int i = 0; i < menuItems.Count; i++) Theme.SetFocused(menuItems[i].Box, i == menuIndex);
        }

        private void ToggleBookmark()
        {
            var bm = FindBookmark(view.Address);
            if (bm != null) { bookmarks.Remove(bm); Toast("Bookmark removed"); }
            else
            {
                string title = string.IsNullOrWhiteSpace(view.Title) ? view.Address : view.Title;
                bookmarks.Add(new BrowserEntry { Title = title, Url = view.Address, Time = Now() });
                Toast("Bookmark added");
            }
            SaveList(BookmarksFile, bookmarks);
        }

        private void ClearData()
        {
            history.Clear();
            SaveList(HistoryFile, history);
            view.ClearCookies();
            Toast("History and cookies cleared");
        }

        private BrowserEntry FindBookmark(string url)
        {
            return bookmarks.FirstOrDefault(b => string.Equals(b.Url, url, StringComparison.OrdinalIgnoreCase));
        }

        // ----- history / persistence -----

        private void AddHistory(string url, string title)
        {
            history.RemoveAll(h => string.Equals(h.Url, url, StringComparison.OrdinalIgnoreCase));
            history.Insert(0, new BrowserEntry { Url = url, Title = string.IsNullOrWhiteSpace(title) ? url : title, Time = Now() });
            if (history.Count > 200) history.RemoveRange(200, history.Count - 200);
            SaveList(HistoryFile, history);
        }

        private void OnTitleChanged(string title)
        {
            if (string.IsNullOrWhiteSpace(title) || history.Count == 0) return;
            var top = history[0];
            if (!string.Equals(top.Url, view.Address, StringComparison.OrdinalIgnoreCase) || top.Title == title) return;
            top.Title = title;
            SaveList(HistoryFile, history);
        }

        private static long Now() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds; }

        private static List<BrowserEntry> LoadList(string file)
        {
            try
            {
                lock (fileLock)
                {
                    if (File.Exists(file))
                        return new JavaScriptSerializer().Deserialize<List<BrowserEntry>>(File.ReadAllText(file, Encoding.UTF8)) ?? new List<BrowserEntry>();
                }
            }
            catch (Exception ex) { logger.Error(ex, "SeleneBrowser: cannot read " + file); }
            return new List<BrowserEntry>();
        }

        // Serializes on the UI thread, writes on a worker so the UI never waits for the disk.
        private static void SaveList(string file, List<BrowserEntry> list)
        {
            string json = new JavaScriptSerializer().Serialize(list);
            Task.Run(delegate
            {
                try
                {
                    lock (fileLock)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(file));
                        File.WriteAllText(file, json, Encoding.UTF8);
                    }
                }
                catch (Exception ex) { logger.Error(ex, "SeleneBrowser: cannot write " + file); }
            });
        }

        private static List<BrowserEntry> DefaultBookmarks()
        {
            var names = new[] { "Google", "Wikipedia", "Reddit", "GitHub", "Twitch" };
            var urls = new[] { "https://www.google.com/", "https://www.wikipedia.org/", "https://www.reddit.com/", "https://github.com/", "https://www.twitch.tv/" };
            return names.Select((n, i) => new BrowserEntry { Title = n, Url = urls[i], Time = Now() }).ToList();
        }

        // ----- start page -----

        private void GoHome()
        {
            // the start page in the theme's own colors (palette read now, so it follows the chosen palette)
            Color ink = Theme.InkColor, glass = Theme.GlassColor, scrim = Theme.ScrimColor, focus = Theme.FocusColor;
            var sb = new StringBuilder();
            sb.Append("<!doctype html><html><head><meta charset='utf-8'><title>Home</title><style>")
              .Append(":root{--ink:").Append(Theme.Css(ink, 1)).Append(";--ink60:").Append(Theme.Css(ink, 0.6))
              .Append(";--line:").Append(Theme.Css(ink, 0.16)).Append(";--glass:").Append(Theme.Css(glass, 0.72))
              .Append(";--well:").Append(Theme.Css(scrim, 0.4)).Append(";--focus:").Append(Theme.Css(focus, 1)).Append("}")
              .Append("html{min-height:100%;background:").Append(Theme.BackdropCss()).Append("}")
              .Append("body{margin:0;padding:56px 72px;color:var(--ink);font:22px 'Segoe UI',sans-serif;background:transparent;")
              .Append("-webkit-font-smoothing:antialiased}")
              .Append("h1{font:600 44px 'Segoe UI Variable Display','Segoe UI',sans-serif;margin:0 0 28px}")
              .Append("h2{font:600 15px 'Segoe UI',sans-serif;letter-spacing:.12em;text-transform:uppercase;margin:44px 0 16px;color:var(--ink60)}")
              .Append("form{margin:0}input{width:100%;box-sizing:border-box;font:24px 'Segoe UI',sans-serif;padding:18px 30px;border-radius:999px;")
              .Append("border:1px solid var(--line);background:var(--well);color:var(--ink);outline:none}")
              .Append("input::placeholder{color:var(--ink60)}")
              .Append(".g{display:grid;grid-template-columns:repeat(auto-fill,minmax(300px,1fr));gap:20px}")
              .Append("a{display:flex;align-items:center;gap:18px;padding:18px 22px;background:var(--glass);border:1px solid var(--line);border-radius:16px;")
              .Append("color:var(--ink);text-decoration:none;overflow:hidden;white-space:nowrap}")
              .Append("a i{flex:none;width:44px;height:44px;border-radius:50%;background:var(--line);display:flex;align-items:center;justify-content:center;")
              .Append("font:600 20px 'Segoe UI Variable Display','Segoe UI',sans-serif;font-style:normal;color:var(--focus)}")
              .Append("a span{min-width:0;flex:1;overflow:hidden;text-overflow:ellipsis;font-family:'Segoe UI Variable Display','Segoe UI',sans-serif;font-weight:600}")
              .Append("small{display:block;color:var(--ink60);font:17px 'Segoe UI',sans-serif;margin-top:4px;overflow:hidden;text-overflow:ellipsis}")
              .Append("</style></head><body><h1>Browser</h1><form action='").Append(SearchAction).Append("'>")
              .Append("<input name='q' placeholder='Search the web' autocomplete='off'></form>");
            AppendTiles(sb, "Bookmarks", bookmarks);
            AppendTiles(sb, "Recent", history.Take(12).ToList());
            sb.Append("</body></html>");
            view.Navigate("data:text/html;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(sb.ToString())));
        }

        private static void AppendTiles(StringBuilder sb, string heading, List<BrowserEntry> items)
        {
            if (items.Count == 0) return;
            sb.Append("<h2>").Append(heading).Append("</h2><div class='g'>");
            foreach (var e in items)
            {
                string host = e.Url;
                Uri u;
                if (Uri.TryCreate(e.Url, UriKind.Absolute, out u)) host = u.Host;
                string name = e.Title ?? e.Url ?? "";
                string letter = name.Trim().Length > 0 ? name.Trim().Substring(0, 1).ToUpperInvariant() : "?";
                sb.Append("<a href='").Append(WebUtility.HtmlEncode(e.Url)).Append("'><i>").Append(WebUtility.HtmlEncode(letter)).Append("</i><span>")
                  .Append(WebUtility.HtmlEncode(name))
                  .Append("<small>").Append(WebUtility.HtmlEncode(host)).Append("</small></span></a>");
            }
            sb.Append("</div>");
        }

        // URL as typed, or a web search when it does not look like an address.
        private static string ResolveInput(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim();
            if (HasScheme(text)) return text;
            bool looksLikeHost = text.IndexOf(' ') < 0 && (text.Contains(".") || text.StartsWith("localhost", StringComparison.OrdinalIgnoreCase));
            return looksLikeHost ? "https://" + text : SearchUrl + Uri.EscapeDataString(text);
        }

        private static bool HasScheme(string t)
        {
            return t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || t.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("file:", StringComparison.OrdinalIgnoreCase) || t.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
        }

        // ----- small helpers -----

        private static Dictionary<string, object> ParseJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try { return new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>; }
            catch { return null; }
        }

        private static bool ParsePoint(string json, out double x, out double y)
        {
            x = y = 0;
            var d = ParseJson(json);
            if (d == null || !d.ContainsKey("x") || !d.ContainsKey("y")) return false;
            x = Convert.ToDouble(d["x"], CultureInfo.InvariantCulture);
            y = Convert.ToDouble(d["y"], CultureInfo.InvariantCulture);
            return true;
        }

        private void After(int ms, Action action)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += delegate
            {
                t.Stop();
                try { action(); } catch (Exception ex) { logger.Error(ex, "SeleneBrowser: delayed action"); }
            };
            t.Start();
        }

        private void Toast(string message)
        {
            toastText.Text = message;
            toast.Visibility = Visibility.Visible;
            var stamp = ++toastStamp;
            After(1800, delegate { if (stamp == toastStamp) toast.Visibility = Visibility.Collapsed; });
        }
        private int toastStamp;

        private void UpdateHints()
        {
            string h;
            if (keyboard.IsOpen) h = OnScreenKeyboard.Hints;
            else if (mode == Mode.Menu) h = settings != null && settings.IsOpen ? "D-pad Select    A Switch on/off    B Back" : "D-pad Select    A Choose    B Close";
            else if (mode == Mode.Bar) h = "D-pad Select    A Open    Y / B Back to page";
            else if (mode == Mode.Cursor) h = "R-stick Move    A Click    LT/RT Scroll    D-pad Focus mode    B Back    Y Top bar    Start Menu";
            else h = "D-pad Move    A Select    B Back    X Reload    Y Top bar    LB/RB History    LT/RT Scroll    R-stick Cursor    L3/R3 Zoom    Start Menu";
            if (h == hints) return;
            hints = h;
            hintText.Show(h);
            if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("Hints"));
        }

        // ----- layout -----

        private UIElement BuildLayout()
        {
            var root = new Grid { Background = Theme.Background };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // top bar: round icon buttons and the address pill, Selene's pills (glass at rest, the accent when focused)
            var bar = new Grid { Margin = new Thickness(18, 12, 18, 10) };
            AddBarItem(bar, "Back", 0xE5C4, delegate { view.GoBack(); EnterFocus(false); });          // arrow_back
            AddBarItem(bar, "Forward", 0xE5C8, delegate { view.GoForward(); EnterFocus(false); });    // arrow_forward
            AddBarItem(bar, "Reload", 0xE5D5, delegate { view.Reload(); EnterFocus(false); });        // refresh
            AddBarItem(bar, "Home", 0xE88A, delegate { GoHome(); EnterFocus(false); });               // home
            var addr = Theme.Tile("", 22, 0xE8B6, Look.Pill);   // search
            Theme.AdoptLabel(addr, addressText);
            Theme.AlignLeft(addr);
            Theme.SetFocusScale(addr, 1.015);
            addr.Padding = new Thickness(24, 0, 24, 1);
            addr.Height = 56;
            addr.Margin = new Thickness(10, 0, 10, 0);
            addr.HorizontalAlignment = HorizontalAlignment.Stretch;
            AddBarItem(bar, addr, EditAddress, true);
            AddBarItem(bar, "Zoom -", 0xE900, delegate { ZoomBy(-ZoomStep); });   // zoom_out
            AddBarItem(bar, "Zoom +", 0xE8FF, delegate { ZoomBy(ZoomStep); });    // zoom_in
            Grid.SetRow(bar, 0);
            root.Children.Add(bar);
            topBar = bar;

            page.Children.Add(view.Element);

            cursorSprite = new System.Windows.Shapes.Polygon
            {
                Points = new PointCollection { new Point(0, 0), new Point(0, 30), new Point(8, 23), new Point(14, 35), new Point(19, 32), new Point(13, 21), new Point(23, 21) },
                Fill = Theme.Ink,
                Stroke = Theme.ScrimA(0.9),
                StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 0, BlurRadius = 12, Opacity = 0.5, Color = Theme.FocusColor }
            };
            cursorLayer.Children.Add(cursorSprite);
            page.Children.Add(cursorLayer);

            Theme.Glass(toast, 26);
            toast.Padding = new Thickness(30, 12, 30, 14);
            toast.Margin = new Thickness(0, 0, 0, 48);
            toast.HorizontalAlignment = HorizontalAlignment.Center;
            toast.VerticalAlignment = VerticalAlignment.Bottom;
            toast.Visibility = Visibility.Collapsed;
            toast.IsHitTestVisible = false;
            toast.Child = toastText;
            page.Children.Add(toast);

            // Start menu: the page dimmed, a glass card with the browser's name over its rows
            Theme.Glass(menuBox, 28);
            menuBox.Padding = new Thickness(22, 24, 22, 22);
            menuBox.MinWidth = 480;
            menuBox.HorizontalAlignment = HorizontalAlignment.Center;
            menuBox.VerticalAlignment = VerticalAlignment.Center;
            var menuStack = new StackPanel();
            var menuTitle = Theme.Title("Browser", 30);
            menuTitle.Margin = new Thickness(24, 0, 24, 14);
            menuStack.Children.Add(menuTitle);
            menuStack.Children.Add(menuList);
            menuBox.Child = menuStack;
            menuShade.Background = Theme.ScrimA(0.62);
            menuShade.Visibility = Visibility.Collapsed;
            menuShade.Children.Add(menuBox);
            page.Children.Add(menuShade);
            page.Children.Add(keyboard);

            Grid.SetRow(page, 1);
            root.Children.Add(page);

            hintText.Margin = new Thickness(24, 10, 40, 14);
            Grid.SetRow(hintText, 2);
            root.Children.Add(hintText);
            UpdateAddress();
            return root;
        }

        // an icon button (its word when the theme's icon font is missing)
        private void AddBarItem(Grid bar, string label, int icon, Action action)
        {
            Border b = Theme.HasIcon(icon) ? Theme.Tile(null, 24, icon, Look.Pill) : Theme.Tile(label, 22);
            if (Theme.HasIcon(icon)) { b.Width = 56; b.Height = 56; b.Padding = new Thickness(0); }
            else b.Padding = new Thickness(20, 10, 20, 11);
            b.Margin = new Thickness(5, 0, 5, 0);
            b.VerticalAlignment = VerticalAlignment.Center;
            AddBarItem(bar, b, action, false);
        }

        private void AddBarItem(Grid bar, Border item, Action action, bool stretch)
        {
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = stretch ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            Grid.SetColumn(item, bar.ColumnDefinitions.Count - 1);
            bar.Children.Add(item);
            barItems.Add(item);
            barActions.Add(action);
        }

        // Spatial-navigation / highlight shim, injected once per page load. %ACCENT% = theme accent color.
        private const string ShimSource = @"(function(){
if(window.__nomadNav) return;
var ACCENT='%ACCENT%';
var SEL='a[href],button,input:not([type=hidden]),textarea,select,summary,[role=button],[role=link],[role=tab],[role=menuitem],[role=checkbox],[role=radio],[role=switch],[role=option],[role=combobox],[tabindex],[onclick],[contenteditable=true],[contenteditable=""""],video,audio';
var NATIVE={A:1,BUTTON:1,INPUT:1,TEXTAREA:1,SELECT:1,SUMMARY:1};
var hl=null, cur=null, enabled=true;

// highlight box (never touches the page's own styles)
function ensureHl(){
 if(hl&&hl.isConnected) return hl;
 hl=document.createElement('div');
 hl.style.cssText='position:fixed;z-index:2147483647;pointer-events:none;box-sizing:border-box;border:3px solid '+ACCENT+';border-radius:12px;box-shadow:0 0 18px '+ACCENT+'8C,0 0 0 1px rgba(0,0,0,.45),inset 0 0 0 1px rgba(0,0,0,.45);display:none;margin:0;padding:0;transition:left .14s cubic-bezier(.2,.7,.3,1),top .14s cubic-bezier(.2,.7,.3,1),width .14s cubic-bezier(.2,.7,.3,1),height .14s cubic-bezier(.2,.7,.3,1)';
 document.documentElement.appendChild(hl);
 return hl;
}
// offset of a (possibly nested) frame document inside the top viewport
function frameOffset(doc){
 var x=0,y=0,w=doc.defaultView;
 while(w&&w!==window&&w.frameElement){ var f=w.frameElement, r=f.getBoundingClientRect(); x+=r.left+f.clientLeft; y+=r.top+f.clientTop; w=w.parent; }
 return {x:x,y:y};
}
function absRect(c){
 var r=c.el.getBoundingClientRect(), o=frameOffset(c.el.ownerDocument);
 return {l:r.left+o.x,t:r.top+o.y,w:r.width,h:r.height};
}
function place(){
 if(!cur||!cur.el.isConnected||!enabled){ if(hl) hl.style.display='none'; return; }
 var r=absRect(cur), h=ensureHl(), s=h.style;
 // Selene's ring: 6 px out from the element, its corners following the element's own
 var rad=10; try{ rad=Math.max(10,Math.min(40,(parseFloat(getComputedStyle(cur.el).borderTopLeftRadius)||0)+6)); }catch(e){}
 s.borderRadius=rad+'px';
 s.left=(r.l-6)+'px'; s.top=(r.t-6)+'px'; s.width=(r.w+12)+'px'; s.height=(r.h+12)+'px'; s.display='block';
}
function add(el,doc,ox,oy,out){
 var r=el.getBoundingClientRect();
 if(r.width<4||r.height<4) return;
 var vw=window.innerWidth, vh=window.innerHeight, l=r.left+ox, t=r.top+oy;
 if(l>vw*2||l+r.width<-vw||t>vh*2||t+r.height<-vh) return;
 if(r.width>vw*0.95&&r.height>vh*0.9) return;
 var cs=doc.defaultView.getComputedStyle(el);
 if(cs.visibility==='hidden'||cs.pointerEvents==='none') return;
 var inView=l<vw&&l+r.width>0&&t<vh&&t+r.height>0;
 if(inView){ // skip elements covered by something else
  var px=Math.min(Math.max(l+r.width/2,1),vw-2)-ox, py=Math.min(Math.max(t+r.height/2,1),vh-2)-oy;
  var p=doc.elementFromPoint(px,py);
  if(p&&!(p===el||el.contains(p)||p.contains(el))) return;
 }
 out.push({el:el,l:l,t:t,w:r.width,h:r.height,inView:inView});
}
function collect(doc,ox,oy,out,depth){
 var list;
 try{ list=doc.querySelectorAll(SEL); }catch(e){ return; }
 for(var i=0;i<list.length&&out.length<1500;i++){
  var el=list[i];
  if(el.disabled||(el.getAttribute('tabindex')==='-1'&&!NATIVE[el.tagName])) continue;
  add(el,doc,ox,oy,out);
 }
 var fr=doc.querySelectorAll('iframe,frame');
 for(var j=0;j<fr.length&&depth<3;j++){
  var d=null; try{ d=fr[j].contentDocument; }catch(e){}
  if(!d){ add(fr[j],doc,ox,oy,out); continue; } // cross-origin frame: one clickable target
  var r=fr[j].getBoundingClientRect();
  collect(d,ox+r.left+fr[j].clientLeft,oy+r.top+fr[j].clientTop,out,depth+1);
 }
}
function setCur(item){ cur={el:item.el}; reveal(item); place(); setTimeout(place,160); }
function reveal(item){
 var vw=window.innerWidth, vh=window.innerHeight;
 if(item.l<0||item.t<0||item.l+item.w>vw||item.t+item.h>vh){
  try{ item.el.scrollIntoView({block:'center',inline:'center',behavior:'instant'}); }catch(e){}
 }
}
function find(list){
 if(!cur||!cur.el.isConnected) return null;
 for(var i=0;i<list.length;i++) if(list[i].el===cur.el) return list[i];
 return null;
}
function gap(a1,a2,b1,b2){ return b1>=a2?b1-a2:(b2<=a1?a1-b2:0); }
// nearest ancestor that can still scroll in direction d (falls back to the document scroller)
function scroller(el,d){
 var vert=(d==='up'||d==='down'), fwd=(d==='down'||d==='right'), doc=el.ownerDocument, n;
 function can(n){
  var pos=vert?n.scrollTop:n.scrollLeft, max=vert?n.scrollHeight-n.clientHeight:n.scrollWidth-n.clientWidth;
  return fwd?pos<max-1:pos>0;
 }
 for(n=el.parentElement;n&&n!==doc.body&&n!==doc.documentElement;n=n.parentElement){
  var cs=doc.defaultView.getComputedStyle(n), ov=vert?cs.overflowY:cs.overflowX;
  if((ov==='auto'||ov==='scroll'||ov==='overlay')&&can(n)) return n;
 }
 var se=doc.scrollingElement;
 return se&&can(se)?se:null;
}
function deepActive(){
 var a=document.activeElement;
 try{ while(a&&a.tagName==='IFRAME'){ a=a.contentDocument.activeElement; } }catch(e){ return null; }
 return a&&a!==document.body&&a!==document.documentElement?a:null;
}

window.__nomadNav={
 // highlight something visible (keep the current element if it is still on screen)
 first:function(){
  enabled=true;
  var list=[]; collect(document,0,0,list,0);
  var c=find(list);
  if(c&&c.inView){ place(); return 'keep'; }
  var best=null,bs=1e18;
  for(var i=0;i<list.length;i++){ var it=list[i]; if(!it.inView) continue; var s=Math.max(it.t,0)+Math.max(it.l,0)*0.3; if(s<bs){ bs=s; best=it; } }
  if(!best){ cur=null; place(); return 'none'; }
  setCur(best); return 'new';
 },
 // highlight the element at / nearest to a viewport point (css px)
 focusNear:function(x,y){
  enabled=true;
  var list=[]; collect(document,0,0,list,0);
  var best=null,bs=1e18;
  for(var i=0;i<list.length;i++){
   var it=list[i]; if(!it.inView) continue;
   var dx=Math.max(it.l-x,0,x-(it.l+it.w)), dy=Math.max(it.t-y,0,y-(it.t+it.h)), s=dx*dx+dy*dy;
   if(s<bs){ bs=s; best=it; }
  }
  if(best){ setCur(best); return 'new'; }
  cur=null; place(); return 'none';
 },
 move:function(d){
  enabled=true;
  var list=[]; collect(document,0,0,list,0);
  var c=find(list);
  if(!c) return this.first();
  var best=null,bs=1e18, ccx=c.l+c.w/2, ccy=c.t+c.h/2;
  for(var i=0;i<list.length;i++){
   var it=list[i]; if(it===c||it.el.contains(c.el)||c.el.contains(it.el)) continue;
   var dx=it.l+it.w/2-ccx, dy=it.t+it.h/2-ccy, g, x;
   if(d==='right'){ if(dx<=1) continue; g=Math.max(0,it.l-(c.l+c.w)); x=gap(c.t,c.t+c.h,it.t,it.t+it.h); }
   else if(d==='left'){ if(dx>=-1) continue; g=Math.max(0,c.l-(it.l+it.w)); x=gap(c.t,c.t+c.h,it.t,it.t+it.h); }
   else if(d==='down'){ if(dy<=1) continue; g=Math.max(0,it.t-(c.t+c.h)); x=gap(c.l,c.l+c.w,it.l,it.l+it.w); }
   else { if(dy>=-1) continue; g=Math.max(0,c.t-(it.t+it.h)); x=gap(c.l,c.l+c.w,it.l,it.l+it.w); }
   var s=g+x*3+(d==='left'||d==='right'?Math.abs(dy):Math.abs(dx))*0.1;
   if(s<bs){ bs=s; best=it; }
  }
  if(best){ setCur(best); return 'moved'; }
  var sc=scroller(c.el,d);
  if(!sc) return 'edge';
  var amt=0.6*((d==='up'||d==='down')?sc.clientHeight:sc.clientWidth);
  sc.scrollBy({top:d==='down'?amt:d==='up'?-amt:0,left:d==='right'?amt:d==='left'?-amt:0,behavior:'instant'});
  place(); setTimeout(place,160);
  return 'scrolled';
 },
 hide:function(){ enabled=false; place(); },
 show:function(){ enabled=true; place(); },
 // center of the highlighted element in viewport css px, clamped to the viewport; null if nothing is highlighted
 info:function(){
  if(!cur||!cur.el.isConnected) return null;
  var r=absRect(cur);
  return JSON.stringify({x:Math.min(Math.max(r.l+r.w/2,1),window.innerWidth-2),y:Math.min(Math.max(r.t+r.h/2,1),window.innerHeight-2)});
 },
 // the focused text field (after a click), or null
 editable:function(){
  var e=deepActive(); if(!e) return null;
  var tag=e.tagName, ty=(e.type||'').toLowerCase(), multi=false;
  if(tag==='TEXTAREA') multi=e.rows>1; // one-row textareas (e.g. Google search) behave like single-line inputs
  else if(tag==='INPUT'){ if(!/^(|text|search|url|email|tel|password|number)$/.test(ty)) return null; }
  else if(e.isContentEditable) multi=true;
  else return null;
  var v=e.value!==undefined?e.value:e.innerText;
  return JSON.stringify({value:v||'',multi:multi,password:ty==='password',label:e.getAttribute('aria-label')||e.placeholder||e.name||''});
 },
 selectAll:function(){
  var e=deepActive(); if(!e) return 'none';
  try{ if(e.select) e.select(); else e.ownerDocument.execCommand('selectAll'); }catch(x){}
  return 'ok';
 }
};
// popups and target=_blank open in this view
document.addEventListener('click',function(ev){
 var a=ev.target&&ev.target.closest?ev.target.closest('a[target]'):null;
 if(a&&a.target!=='_self'&&a.target!=='_top'&&a.target!=='_parent') a.target='_self';
},true);
window.open=function(u){ if(u) location.href=u; return null; };
window.addEventListener('scroll',place,true);
window.addEventListener('resize',place);
})();";
    }

    public class SeleneBrowserPlugin : GenericPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        /// <summary>Plugin user-data folder (bookmarks, history, caches). Set once in the constructor.</summary>
        public static string DataDir { get; private set; }

        public override Guid Id
        {
            get { return Guid.Parse("7362a822-4c12-4880-a054-0b2932af6ae7"); }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint GetErrorMode();

        public SeleneBrowserPlugin(IPlayniteAPI api) : base(api)
        {
            // Couch mode: a crashing Chromium helper (Playnite.BrowserProcess.exe - any add-on's page) must not put up a
            // Windows "EXCEPTION / OK" box that only a mouse can close; Chromium replaces the helper by itself.
            // Child processes inherit the error mode, so it is set here, before any web page starts.
            try { SetErrorMode(GetErrorMode() | 0x0001 /* SEM_FAILCRITICALERRORS */ | 0x0002 /* SEM_NOGPFAULTERRORBOX */); }
            catch (Exception ex) { logger.Warn("SeleneBrowser: SetErrorMode: " + ex.Message); }
            AddCustomElementSupport(new AddCustomElementSupportArgs
            {
                SourceName = "SeleneBrowser",
                ElementList = new List<string> { "BrowserSurface", "YouTubeSurface" }
            });
            Properties = new GenericPluginProperties { HasSettings = false };
            try
            {
                DataDir = GetPluginUserDataPath();
                Directory.CreateDirectory(DataDir);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "SeleneBrowser: plugin data folder");
                DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nomad");
            }
            // Chromium flags cannot be added here: Playnite initializes CEF before plugins load, so
            // trusted input events from CefView replace the old autoplay-policy switch.
            logger.Info("SeleneBrowser: plugin loaded");
        }

        public override Control GetGameViewControl(GetGameViewControlArgs args)
        {
            if (args.Name == "BrowserSurface") return new BrowserControl();
            if (args.Name == "YouTubeSurface") return new YouTubeControl();
            return null;
        }
    }
}
