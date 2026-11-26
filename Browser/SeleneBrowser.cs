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
            public bool Keep;      // opens another menu: the menu stays
            public Border Box;
        }

        private const string SearchAction = "https://www.google.com/search";
        private const string SearchUrl = SearchAction + "?q=";
        private const double ZoomStep = 0.5;
        internal static readonly object fileLock = new object();   // guards bookmarks.json / history.json (CardInfo reads them too)

        private const int MaxTabs = 5;
        private readonly List<BrowserTab> tabs = new List<BrowserTab>();
        private BrowserTab active;
        private long tabClock;
        // the tab pills; the row around them (with the LB / RB plates) only shows while there is more than one tab
        private readonly StackPanel tabStrip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            UseLayoutRounding = true,
            IsHitTestVisible = false
        };
        private readonly StackPanel tabRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        // the rest of the control keeps talking to the active page through this alias
        private IPageView view { get { return active == null ? null : active.View; } }
        private StackPanel topBar;   // the floating header: the bar of pills and, under it, the tab row
        private AddressPill addrPill;
        private ClockChip barClock;
        private Fader headerFade, hintFade, toastFade, menuCardFade;
        private System.Windows.Shapes.Ellipse cursorHalo;
        private readonly OnScreenKeyboard keyboard = new OnScreenKeyboard();
        private readonly PadInput pad = new PadInput();
        private PageScroller scroller;   // the left stick, the triggers and an edge push scroll the page through this (PageScroller.cs)
        private long lastStickAt;
        private readonly RepeatLimiter limiter = new RepeatLimiter();
        private readonly DispatcherTimer poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private string shim;   // rebuilt when the theme (the highlight's color) changes
        private readonly Grid page = new Grid();
        private System.Windows.Shapes.Polygon cursorSprite;
        private readonly Canvas cursorLayer =new Canvas { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        private readonly Border toast = new Border();
        private readonly TextBlock toastText = Theme.Text("", 22, Theme.Ink);
        private readonly RestChip restChip = new RestChip();   // passive pad-rest reminder (AppDomain slot)
        private readonly Border menuBox = new Border();
        private readonly Grid menuShade = new Grid();
        private readonly StackPanel menuList = new StackPanel();
        private readonly HintBar hintText = new HintBar(21, HorizontalAlignment.Left);
        private readonly List<Border> barItems = new List<Border>();
        private readonly List<Action> barActions = new List<Action>();
        private readonly List<MenuEntry> menuItems = new List<MenuEntry>();
        private List<BrowserEntry> bookmarks;
        private List<BrowserEntry> history;

        private Mode mode = Mode.Focus;
        private bool shown;
        private readonly DispatcherTimer disposeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(180) };
        private int barIndex;
        private int menuIndex;
        private TextBlock menuTitleText;
        private double zoom;
        private Point cursor;
        private double held;
        private long lastTick, lastMove;
        private string hints = "";

        public event PropertyChangedEventHandler PropertyChanged;
        public string Hints { get { return hints; } }

        private static string BookmarksFile { get { return Path.Combine(SeleneBrowserPlugin.DataDir, "bookmarks.json"); } }
        private static string HistoryFile { get { return Path.Combine(SeleneBrowserPlugin.DataDir, "history.json"); } }
        private double Scale { get { return Math.Max(0.25, Math.Min(5, BaseScale() * Math.Pow(1.2, zoom))); } } // css px -> DIP (the page's real zoom factor)

        // The screen's base page scale, under the user's own zoom. Auto = the Viewbox stretch (2 on a 4K TV at 100%), never below 1, so
        // the page looks like it does on a 1080p screen and the text stays readable from the couch; or a fixed factor from the settings.
        private double BaseScale()
        {
            double fixedScale = BrowserSettings.PageScale;
            if (fixedScale > 0) return fixedScale;
            return Math.Max(1, CssPerDesign()) * BrowserSettings.PageBoost;
        }

        // Pushes the base scale to a tab's engine (also called when the settings change, the browser is shown, and a page has loaded).
        private void ApplyBaseScale(BrowserTab tab)
        {
            if (tab == null || tab.ViewDisposed || tab.View == null) return;
            try { tab.View.SetBaseScale(BaseScale()); }
            catch (Exception ex) { logger.Warn("SeleneBrowser: base scale: " + ex.Message); }
        }

        private void ApplyBaseScaleAll()
        {
            cssPerDesignAt = Environment.TickCount - 100000;   // measure the Viewbox again
            foreach (var t in tabs) ApplyBaseScale(t);
        }

        // The theme is laid out at 1920 x 1080 and the window's Viewbox stretches it to the screen. The page is a native window
        // sized in real DIPs, so one design unit is Viewbox-stretch CSS pixels (1 at 1080p/100%, 1.33 at 1440p/100%, 2 at 4K/100%):
        // the cursor lives in design units and must be converted before it is sent to the page.
        private double cssPerDesign = 1;
        private long cssPerDesignAt = -10000;
        private double CssPerDesign()
        {
            long now = Environment.TickCount;
            if (now - cssPerDesignAt < 1000) return cssPerDesign;
            cssPerDesignAt = now;
            try
            {
                Window win = Window.GetWindow(this);
                if (win == null && Application.Current != null) win = Application.Current.MainWindow;
                FrameworkElement cur = win == null ? null : win.Content as FrameworkElement;
                for (int i = 0; cur != null && i < 16; i++)
                {
                    Viewbox box = cur as Viewbox;
                    FrameworkElement inner = box == null ? null : box.Child as FrameworkElement;
                    if (inner != null && inner.ActualWidth > 0 && box.ActualWidth > 0)
                    {
                        // a Uniform Viewbox on a non-16:9 screen (ultrawide, 16:10) stretches by the smaller of the two ratios
                        double s = box.ActualWidth / inner.ActualWidth;
                        if (inner.ActualHeight > 0 && box.ActualHeight > 0) s = Math.Min(s, box.ActualHeight / inner.ActualHeight);
                        cssPerDesign = s >= 0.25 && s <= 8 ? s : 1;
                        return cssPerDesign;
                    }
                    cur = VisualTreeHelper.GetChildrenCount(cur) == 1 ? VisualTreeHelper.GetChild(cur, 0) as FrameworkElement : null;
                }
            }
            catch { }
            return cssPerDesign;
        }
        // the start page is a data: URL; WebView2 reports such pages with an empty Source after history navigation
        private bool IsHome
        {
            get
            {
                string a = view.Address ?? "";
                return a.Length == 0 || a.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || a == "about:blank";
            }
        }

        // Windowed WebView2: the composition control rasterizes at design size and blurs under the
        // theme's Viewbox. The browser chrome lives in a separate overlay window (see InitChromeOverlay).
        private static IPageView CreateView() { return PageViews.Create(true); }

        private void OnFullScreenChanged(object sender, EventArgs e)
        {
            bool fs = view.IsFullScreen;
            WakePointer();   // (shown again on leaving fullscreen; the idle timer starts over on entering it)
            ApplyBars();   // top bar / hints: hidden in fullscreen or while the auto-hide has stepped aside
            if (fs && mode == Mode.Bar) EnterFocus(false);
            // left fullscreen: put the highlight back once the player has shrunk to its place in the page
            if (!fs && mode == Mode.Focus) { After(250, delegate { Script("show()"); }); After(700, delegate { Script("show()"); }); }
        }

        // every tab owns its engine; events carry the tab in a closure so background pages never move the active UI
        private void HookTab(BrowserTab tab)
        {
            IPageView v = tab.View;
            v.LoadEnd += delegate(string url) { OnLoadEnd(tab, url); };
            v.AddressChanged += delegate(string url) { OnTabAddress(tab, url); };
            v.TitleChanged += delegate(string title) { OnTabTitle(tab, title); };
            v.FullScreenChanged += delegate(object s, EventArgs e) { OnTabFullScreen(tab); };
            v.NewWindowRequested += delegate(string url)
            {
                BrowserTab popup = NewTab(url, true);
                popup.Opener = tab;
                if (shown && tab == active) ActivateTab(popup);   // a background tab or a hidden browser never takes the screen
            };
            v.PageNotice += delegate(string message) { if (tab == active && shown) Toast(message); };
            var edge = v as EdgeView;
            if (edge != null)
            {
                edge.InitFailed += delegate { OnEdgeInitFailed(tab); };
                edge.BrowserProcessGone += delegate { OnTabEngineGone(tab, edge); };
                HookEdgeTools(tab, edge);
            }
        }

        // The WebView2 browser process died: every call on that view would fail quietly from now on. Replace the engine (the page comes
        // back from its address). A tab that is not in front gets its new engine when it is switched to; a second death within
        // 30 s is not retried.
        private void OnTabEngineGone(BrowserTab tab, EdgeView edge)
        {
            try
            {
                if (tab.ViewDisposed || !object.ReferenceEquals(tab.View, edge) || !tabs.Contains(tab)) return;
                int now = Environment.TickCount;
                if (tab.EngineRestarted && unchecked(now - tab.EngineRestartAt) < 30000)
                {
                    logger.Warn("SeleneBrowser: page engine died again within 30 s, not restarting it");
                    return;
                }
                tab.EngineRestarted = true;
                tab.EngineRestartAt = now;
                logger.Warn("SeleneBrowser: page engine process exited, replacing it");
                DisposeTab(tab);
                if (tab == active && shown) RecreateTab(tab);   // hidden: OnShown brings it back
            }
            catch (Exception ex) { logger.Error(ex, "SeleneBrowser: engine restart"); }
        }

        private void OnTabAddress(BrowserTab tab, string url)
        {
            bool wasHome = IsHomeUrl(tab.Url);
            tab.Url = url ?? "";
            if (wasHome != IsHomeUrl(tab.Url)) UpdateTabStrip();   // the start page's pill shows "Home" with its icon
            if (tab != active) return;
            UpdateAddress();
            BarsNewPage(url);
        }

        private static bool IsHomeUrl(string a)
        {
            return string.IsNullOrEmpty(a) || a.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || a == "about:blank";
        }

        private void OnTabTitle(BrowserTab tab, string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return;
            tab.Title = title;
            if (tab == active) UpdateAddress();
            UpdateTabStrip();
            if (history.Count > 0 && string.Equals(history[0].Url, tab.Url, StringComparison.OrdinalIgnoreCase)
                && history[0].Title != title)
            {
                history[0].Title = title;
                // a page that animates its title would rewrite the file every tick: save once it settles (and when the browser is parked)
                historySave.Stop();
                historySave.Start();
            }
        }

        private readonly DispatcherTimer historySave = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };

        private void FlushHistorySave()
        {
            if (!historySave.IsEnabled) return;
            historySave.Stop();
            SaveList(HistoryFile, history);
        }

        private void OnTabFullScreen(BrowserTab tab)
        {
            if (tab == active) OnFullScreenChanged(null, EventArgs.Empty);
        }

        // WebView2 was installed but would not start (broken runtime, policy...): carry on with the CEF engine
        private void OnEdgeInitFailed(BrowserTab tab)
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                try
                {
                    logger.Warn("SeleneBrowser: WebView2 failed to start - switching to CEF");
                    int at = page.Children.IndexOf(tab.View.Element);
                    Visibility wasVisible = tab.View.Element.Visibility;
                    page.Children.Remove(tab.View.Element);
                    tab.View.Dispose();
                    tab.View = new CefView();
                    tab.ViewDisposed = false;
                    tab.View.Element.Visibility = wasVisible;   // otherwise every background tab shows at once
                    page.Children.Insert(Math.Max(0, at), tab.View.Element);
                    ApplyBaseScale(tab);
                    if (tab.Zoom != 0) tab.View.SetZoomLevel(tab.Zoom);
                    tab.View.SetIdle(tab != active);
                    HookTab(tab);
                    if (tab == active) GoHome();
                }
                catch (Exception ex) { logger.Error(ex, "SeleneBrowser: CEF fallback"); }
            }));
        }
        public BrowserControl()
        {
            RemoteCurrent = this;
            shim = BuildShim();
            bookmarks = LoadList(BookmarksFile);
            if (bookmarks.Count == 0) bookmarks = DefaultBookmarks();
            history = LoadList(HistoryFile);
            Focusable = false;
            NewTab(null, false);   // the browser always has at least one tab
            Content = BuildLayout();
            InitChromeOverlay(page);   // the WebView2 HWND region: all in-page chrome must be in a separate window
            InitExtras();
            InitTools();
            scroller = new PageScroller(delegate(string js) { var v = view; if (v != null && v.IsReady) v.ExecuteScript(js); });

            poll.Tick += OnPollTick;
            historySave.Tick += delegate { FlushHistorySave(); };
            // closed for good (hidden / unloaded for a while): free the page engine; Loaded / OnShown bring it back
            disposeTimer.Tick += delegate { DisposeView(); };
            Unloaded += delegate { Park(); };
            Loaded += delegate
            {
                disposeTimer.Stop();
                EnsureView();
                if (!shown) disposeTimer.Start();   // re-attached but not shown: still free the engine soon
            };
            // built when the UI is idle: the first start page encodes the theme wallpaper, which must not hold up Playnite starting
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate { if (active != null && string.IsNullOrEmpty(active.Url) && !active.ViewDisposed) GoHome(); }));
            UpdateHints();
        }

        // ----- pad contract -----

        public bool HandlePad(string button)
        {
            try
            {
                PadActivity.Mark();
                WakePointer();   // any press brings a faded cursor / highlight back
                if (keyboard.IsOpen)
                {
                    keyboard.HandlePad(button);
                    if (!keyboard.IsOpen) UpdateHints();   // Done / Cancel: back to the page's hints
                    return true;   // B on the keyboard cancels it, never closes the browser
                }
                if (panelKind != PanelKind.None) return HandlePanel(button);
                if (linkCard.IsOpen) { if (linkCard.HandlePad(button, limiter)) linkCard.Close(); return true; }
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
            // Only the visible tab may run at full budget: composition hosting captures every tab's
            // frames, and SetIdle(true) also mutes the tab and drops its memory target to Low.
            foreach (var t in tabs)
            {
                if (t.ViewDisposed) continue;
                t.View.SetIdle(t != active);
            }
            ApplyBaseScaleAll();   // the screen (or the Windows scale) may have changed since the last visit
            Presence.Enter("browser");
            shown = true;
            lastTick = clock.ElapsedMilliseconds;
            poll.Start();
            BarsStart();
            InjectShim();
            restChip.Start();
            UpdateHints();
        }

        public void OnHidden() { Park(); CardInfo.Refresh(); }

        // hidden or unloaded: pause every tab, trim memory, and drop the engines if nobody comes back soon
        private void Park()
        {
            shown = false;
            FlushHistorySave();
            if (scroller != null) scroller.Stop();
            BarsStop();
            poll.Stop();
            restChip.Stop();
            Presence.Leave("browser");
            logger.Info("SeleneBrowser: park (" + tabs.Count + " tabs, dispose in 3 min)");
            foreach (var t in tabs)
            {
                if (t.ViewDisposed) continue;
                t.View.ExecuteScript("window.__nomadExitFs&&window.__nomadExitFs()");
                t.View.ExecuteScript("document.querySelectorAll('video,audio').forEach(function(m){try{m.pause()}catch(e){}})");
                t.View.SetIdle(true);
            }
            disposeTimer.Stop();
            disposeTimer.Start();
        }

        private void DisposeView()
        {
            disposeTimer.Stop();
            if (shown) return;
            foreach (var t in tabs) if (!t.ViewDisposed) DisposeTab(t);
        }

        private void DisposeTab(BrowserTab t)
        {
            try
            {
                string a = IsHomeTab(t) ? null : t.View.Address;
                t.ResumeUrl = string.IsNullOrEmpty(a) ? null : a;   // restored if this tab comes back
                page.Children.Remove(t.View.Element);
                logger.Info("SeleneBrowser: dispose tab (resume " + (a ?? "home") + ")");
                t.View.Dispose();
                t.ViewDisposed = true;
            }
            catch (Exception ex) { logger.Error(ex, "SeleneBrowser: dispose view"); }
        }

        // a fresh engine for the tab in front after the disposal; the others come back when they are switched to (five pages
        // starting at once hitched the UI), each keeping its URL so the session survives
        private void EnsureView()
        {
            if (active != null && active.ViewDisposed) RecreateTab(active);
        }

        private void RecreateTab(BrowserTab t)
        {
            try
            {
                logger.Info("SeleneBrowser: recreate tab (resume " + (t.ResumeUrl ?? "home") + ")");
                t.View = CreateView();
                t.ViewDisposed = false;
                page.Children.Insert(0, t.View.Element);
                t.View.Element.Visibility = t == active ? Visibility.Visible : Visibility.Collapsed;
                HookTab(t);
                // a recreated engine starts unmuted / normal memory: apply its state now (EdgeView
                // remembers the request until WebView2 finishes initializing)
                t.View.SetIdle(!shown || t != active);
                string u = t.ResumeUrl;
                t.ResumeUrl = null;
                if (u != null) t.View.Navigate(u); else GoHome(t);
                OnFullScreenChanged(null, EventArgs.Empty);
            }
            catch (Exception ex) { logger.Error(ex, "SeleneBrowser: recreate view"); }
        }

        private bool IsHomeTab(BrowserTab t)
        {
            if (t == null) return true;
            string a = t.View.Address ?? "";
            return a.Length == 0 || a.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || a == "about:blank";
        }

        // ----- tabs -----

        private BrowserTab NewTab(string url, bool evictIfFull)
        {
            if (tabs.Count >= MaxTabs)
            {
                BrowserTab victim = null;
                foreach (var t in tabs)
                    if (t != active && (victim == null || t.LastUsed < victim.LastUsed)) victim = t;
                if (victim == null) victim = active;
                if (victim != null)
                {
                    logger.Info("SeleneBrowser: tab limit " + MaxTabs + " reached, closing least-recent tab");
                    string gone = string.IsNullOrWhiteSpace(victim.Title) ? "a tab" : "\"" + (victim.Title.Length > 24 ? victim.Title.Substring(0, 23) + "\u2026" : victim.Title) + "\"";
                    CloseTab(victim);
                    if (shown) Toast("Tab limit reached - closed " + gone);
                }
            }
            var tab = new BrowserTab { LastUsed = ++tabClock };
            tab.View = CreateView();
            page.Children.Insert(0, tab.View.Element);
            tab.View.Element.Visibility = Visibility.Collapsed;
            HookTab(tab);
            ApplyBaseScale(tab);
            tabs.Add(tab);
            if (active == null) { active = tab; tab.View.Element.Visibility = Visibility.Visible; }
            // a new tab the caller never activates must not keep playing sound or a full renderer budget
            if (tab != active && !tab.ViewDisposed) tab.View.SetIdle(true);
            UpdateTabStrip();
            if (url != null) tab.View.Navigate(ResolveInput(url) ?? url);
            return tab;
        }

        private void CloseTab(BrowserTab tab)
        {
            if (tab == null || !tabs.Contains(tab)) return;
            int closedAt = tabs.IndexOf(tab);
            page.Children.Remove(tab.View.Element);
            if (!tab.ViewDisposed) tab.View.Dispose();
            tabs.Remove(tab);
            if (tabs.Count == 0) { active = null; NewTab(null, false); GoHome(active); }
            else if (active == tab) ActivateTab(tabs[Math.Min(Math.Max(0, closedAt), tabs.Count - 1)]);   // the neighbour that took its place
            UpdateTabStrip();
        }

        private void ActivateTab(BrowserTab tab)
        {
            if (tab == null || !tabs.Contains(tab) || tab == active) return;
            findActive = false;
            if (scroller != null) scroller.Stop();
            BrowserTab previous = active;   // the tab we are leaving; only the active one renders at full budget
            if (active != null && !active.ViewDisposed) active.View.Element.Visibility = Visibility.Collapsed;
            active = tab;
            if (tab.ViewDisposed) RecreateTab(tab);
            tab.View.Element.Visibility = Visibility.Visible;
            tab.LastUsed = ++tabClock;
            zoom = tab.Zoom;
            if (tab.View.IsReady) { ApplyBaseScale(tab); tab.View.SetZoomLevel(zoom); }
            // SetIdle(true) mutes as well as trimming memory, so a background tab cannot keep making sound
            if (!tab.ViewDisposed) tab.View.SetIdle(false);
            if (previous != null && !previous.ViewDisposed) previous.View.SetIdle(true);
            UpdateAddress();
            UpdateTabStrip();
            if (shown && tab.View.IsReady) tab.View.ExecuteScript(shim);   // it may have finished loading while it was in the background
            OnFullScreenChanged(null, EventArgs.Empty);   // top bar / tab strip / hints follow the tab now shown (it may not be fullscreen)
            EnterFocus(false);
        }

        // LB/RB are tabs everywhere (Focus, Cursor and Bar modes), matching the YouTube surface.
        // Page history stays on the top bar's Back / Forward pills and on B.
        private static string TabStep(string b)
        {
            if (b == "LeftShoulder") return "prev";
            if (b == "RightShoulder") return "next";
            return null;
        }

        private void SwitchTab(string step)
        {
            if (step != "prev" && step != "next") return;
            if (tabs.Count < 2) return;
            if (step == "prev") PrevTab(); else NextTab();
            int at = tabs.IndexOf(active);
            if (at >= 0) Toast("Tab " + (at + 1) + " / " + tabs.Count);
        }

        private void NextTab()
        {
            if (tabs.Count < 2) return;
            int at = tabs.IndexOf(active);
            ActivateTab(tabs[(at + 1) % tabs.Count]);
        }

        private void PrevTab()
        {
            if (tabs.Count < 2) return;
            int at = tabs.IndexOf(active);
            ActivateTab(tabs[(at - 1 + tabs.Count) % tabs.Count]);
        }

        private void UpdateTabStrip()
        {
            tabStrip.Children.Clear();
            for (int i = 0; i < tabs.Count; i++)
            {
                BrowserTab t = tabs[i];
                bool home = IsHomeUrl(t.Url);
                string label = t.Key == "news" ? "News" : (home ? "Home" : (string.IsNullOrWhiteSpace(t.Title) ? "Tab " + (i + 1) : t.Title));
                if (label.Length > 28) label = label.Substring(0, 27) + "…";   // one long title must not stretch the strip
                tabStrip.Children.Add(ChromeParts.TabPill(label, t == active, home && t.Key != "news"));
            }
            // the row (with LB / RB at its ends) is only there when there is something to switch to
            tabRow.Visibility = tabs.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }

        public void Navigate(string url)
        {
            var target = ResolveInput(url);
            if (target != null) view.Navigate(target);
            if (active != null) active.Url = target ?? "";
        }

        // dev key (SeleneCore "nomad:phone"): opens the address keyboard, to try the phone keyboard without walking the pad there
        public void DevPhone() { if (!keyboard.IsOpen && shown) EditAddress(); }

        // Entry point for Selene's links. reuseKey "news" keeps one News tab instead of piling articles up.
        public void OpenUrl(string url) { OpenUrl(url, null); }

        public void OpenUrl(string url, string reuseKey)
        {
            EnsureView();
            BrowserTab tab = null;
            if (reuseKey != null)
                foreach (var t in tabs) if (t.Key == reuseKey) { tab = t; break; }
            if (tab == null)
            {
                tab = NewTab(url, true);
                ActivateTab(tab);
            }
            else { ActivateTab(tab); Navigate(url); }
            tab.Key = reuseKey;
            tab.FromTheme = true;
            tab.LastUsed = ++tabClock;
            EnterFocus(false);
            UpdateHints();
            logger.Info("SeleneBrowser: OpenUrl " + url + (reuseKey == null ? "" : " [" + reuseKey + "]"));
        }

        // ----- page modes -----

        private bool HandlePage(string b)
        {
            if (HandleFindKey(b)) return true;
            switch (b)
            {
                case "DPadUp": case "DPadDown": case "DPadLeft": case "DPadRight":
                    if (FromLeftStick(b)) return true;   // the left stick scrolls (OnPollTick); Playnite also reports it as D-pad presses
                    if (!limiter.Allow(b)) return true;
                    if (mode == Mode.Cursor) EnterFocus(true);
                    else Script("move('" + b.Substring(4).ToLowerInvariant() + "')");
                    return true;
                case "A": if (limiter.IsFirst(b)) Activate(); return true;
                case "B": return !limiter.IsFirst(b) || BackOrClose();
                case "X": if (limiter.IsFirst(b)) view.Reload(); return true;
                case "Y": case "Back": if (limiter.IsFirst(b)) EnterBar(); return true;
                case "LeftShoulder":
                case "RightShoulder":
                    if (limiter.IsFirst(b)) SwitchTab(TabStep(b));
                    return true;
                case "TriggerLeft": if (!pad.Connected && limiter.Allow(b)) ScrollPage(-1); return true;   // (with an XInput pad the poll scrolls by how far the trigger is pressed)
                case "TriggerRight": if (!pad.Connected && limiter.Allow(b)) ScrollPage(1); return true;
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
            if (LeaveReaderOnBack()) return true;
            // the start page is the browser's top level: B there always closes (each trip home is a history entry,
            // so going "back" from it only reached another identical start page and B seemed to do nothing)
            if (IsHome) return false;
            BrowserTab t = active;
            if (t != null && t.Key != null) return false;   // a News article: B goes back to the News screen, not through the articles read before
            if (view.CanGoBack) { view.GoBack(); return true; }
            if (t != null && t.Opener != null && tabs.Count > 1)
            {
                BrowserTab opener = t.Opener;
                CloseTab(t);
                if (tabs.Contains(opener)) ActivateTab(opener);
                return true;
            }
            if (t != null && t.FromTheme) return false;     // a Store page and the like: B returns to where it was opened
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

        private void OnLoadEnd(BrowserTab tab, string url)
        {
            ApplyBaseScale(tab);
            tab.Reader = false;   // a new page: reader mode, an open search and the old picture are gone
            if (tab == active && scroller != null) scroller.Stop();
            if (tab == active) findActive = false;
            ApplySiteZoom(tab, url);
            if (tab.Zoom != 0) tab.View.SetZoomLevel(tab.Zoom);
            if (url != null && url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) AddHistory(url, tab.View.Title);
            if (tab != active) { UpdateTabStrip(); return; }
            if (shown) InjectShim();
            UpdateAddress();
            // a picture of the finished page for the tab switcher (a moment later, once it has painted)
            After(1600, delegate { if (shown && tab == active && panelKind == PanelKind.None && mode != Mode.Menu && !keyboard.IsOpen) CaptureThumb(tab, null); });
        }

        // A: trusted click on the highlighted element (focus mode) or at the cursor; text fields then open the keyboard.
        private void Activate()
        {
            if (mode == Mode.Cursor) { double k = CssPerDesign(); ClickAt(cursor.X * k, cursor.Y * k); return; }
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
            // the start page's search pill is the address bar in another place: same keyboard, same suggestions, URLs resolve
            if (d.ContainsKey("id") && StartPage.SearchId.Equals(d["id"] as string))
            {
                view.ExecuteScript("document.activeElement&&document.activeElement.blur&&document.activeElement.blur()");
                EditAddress();
                return;
            }
            bool multi =d.ContainsKey("multi") && (bool)d["multi"];
            bool pw = d.ContainsKey("password") && (bool)d["password"];
            bool more = d.ContainsKey("more") && (bool)d["more"];   // another field follows: Done moves on to it instead of submitting a half-filled form
            string label = d.ContainsKey("label") ? d["label"] as string : "";
            string title = pw ? "Password" : (string.IsNullOrEmpty(label) ? "Type text" : label);
            keyboard.Open(title, d.ContainsKey("value") ? d["value"] as string : "", delegate(string text)
            {
                // select the field's content, replace it with trusted key events, Enter submits single-line fields
                view.EvaluateAsync("window.__nomadNav.selectAll()", delegate
                {
                    if (text.Length == 0) view.PressKey(8); else view.TypeText(text);
                    if (!multi) view.PressKey(more ? 9 : 13);
                });
            }, null, null, pw, multi);
            UpdateHints();
        }

        private void ScrollPage(int dir)
        {
            BarsScrolled(dir, 240);
            double wheel = -dir * 240;
            if (mode == Mode.Cursor) { double k = CssPerDesign(); view.Wheel(cursor.X * k, cursor.Y * k, (int)wheel); return; }
            view.EvaluateAsync("window.__nomadNav?window.__nomadNav.info():null", delegate(string json)
            {
                double x, y;
                if (ParsePoint(json, out x, out y)) view.Wheel(x * Scale, y * Scale, (int)wheel);
                else view.Wheel(page.ActualWidth / 2 * CssPerDesign(), page.ActualHeight / 2 * CssPerDesign(), (int)wheel);
            });
        }

        private void ZoomBy(double delta)
        {
            zoom = Math.Max(-3, Math.Min(5, zoom + delta));
            if (Math.Abs(zoom) < 0.01) zoom = 0;
            if (active != null) active.Zoom = zoom;
            SaveSiteZoom();
            view.SetZoomLevel(zoom);
            Toast("Zoom " + Math.Round(Math.Pow(1.2, zoom) * 100).ToString(CultureInfo.InvariantCulture) + "%");
        }

        // ----- cursor mode (right stick) -----

        private void OnPollTick(object sender, EventArgs e)
        {
            if (!shown || keyboard.IsOpen || mode == Mode.Menu || mode == Mode.Bar) { scroller.Stop(); return; }
            var w = Window.GetWindow(this);
            if (w == null || !w.IsActive) { scroller.Stop(); return; }
            long now = clock.ElapsedMilliseconds;
            double dt = Math.Min(0.05, (now - lastTick) / 1000.0);
            lastTick = now;
            if (!pad.Update()) { scroller.Stop(); HideWhenIdle(now); return; }

            // scrolling: the left stick, the triggers (by how far they are pressed) and the cursor pushed against an edge of the page
            double tx, ty, ex, ey;
            string anchor = "", edgeAnchor;
            PageScroller.Target(pad.LeftX, pad.LeftY, pad.LeftTrigger, pad.RightTrigger, out tx, out ty);
            EdgePush(out ex, out ey, out edgeAnchor);
            if (ex != 0 || ey != 0) { tx += ex; ty += ey; anchor = edgeAnchor; }
            else if (mode == Mode.Cursor)
            {
                double ck = CssPerDesign() / Scale;
                anchor = (cursor.X * ck).ToString("0.#", CultureInfo.InvariantCulture) + "," + (cursor.Y * ck).ToString("0.#", CultureInfo.InvariantCulture);
            }
            bool scrolling = scroller.Update(dt, tx, ty, anchor, now);
            if (scrolling || tx != 0 || ty != 0)
            {
                PadActivity.Mark();   // read here, not by Playnite: it still counts as activity
                WakePointer();
                // (the page tells how far it really moved, for the bars; an engine that cannot tell gets a guess from the speed)
                if (!(view is EdgeView) && ty != 0) BarsScrolled(ty > 0 ? 1 : -1, (int)Math.Max(1, Math.Abs(ty) * page.ActualHeight * dt));
            }

            double mag = Math.Sqrt(pad.RightX * pad.RightX + pad.RightY * pad.RightY);
            if (mag <= 0) { held = 0; if (hoverDirty) ProbeHover(now); HideWhenIdle(now); return; }
            PadActivity.Mark();   // the right stick is read here, not by Playnite: it still counts as activity
            WakePointer();
            if (mode == Mode.Focus) EnterCursor();
            held += dt;
            BarsMoved(dt);
            double speed = 1100 * mag * mag * (1 + Math.Min(held, 1.2) * 1.5) * dt;
            cursor = new Point(
                Math.Max(0, Math.Min(page.ActualWidth - 1, cursor.X + pad.RightX * speed)),
                Math.Max(0, Math.Min(page.ActualHeight - 1, cursor.Y - pad.RightY * speed)));
            PlaceCursor();
            hoverDirty = true;
            ProbeHover(now);
            if (now - lastMove >= 30) { lastMove = now; double k = CssPerDesign(); view.MoveMouse(cursor.X * k, cursor.Y * k); }
        }

        // Playnite reports the left stick as D-pad presses. With a pad the browser can see which one it was: a real D-pad press moves
        // the focus, the stick scrolls (so its presses are dropped here). Keyboard arrows and pads XInput cannot see stay D-pad presses.
        private bool FromLeftStick(string b)
        {
            bool connected, pushed;
            ushort buttons;
            pad.Sample(out connected, out buttons, out pushed);
            if (!connected) return false;
            ushort mask = b == "DPadUp" ? PadInput.DPadUp : b == "DPadDown" ? PadInput.DPadDown : b == "DPadLeft" ? PadInput.DPadLeft : PadInput.DPadRight;
            if ((buttons & mask) != 0) return false;   // the real D-pad is down
            long now = clock.ElapsedMilliseconds;
            if (pushed) { lastStickAt = now; return true; }
            return now - lastStickAt < 150;   // the stick was let go a moment ago: Playnite's last press can still arrive
        }

        private void PlaceCursor()
        {
            Canvas.SetLeft(cursorSprite, cursor.X);
            Canvas.SetTop(cursorSprite, cursor.Y);
            Canvas.SetLeft(cursorHalo, cursor.X - cursorHalo.Width / 2);
            Canvas.SetTop(cursorHalo, cursor.Y - cursorHalo.Height / 2);
        }

        // A soft accent ring grows around the cursor while something clickable is under it. Asking the page is an IPC round trip,
        // so it happens a few times a second while the cursor moves, and once more when it stops.
        private long lastProbe;
        private bool hoverDirty, hoverBusy, hoverOn;

        private void ProbeHover(long now)
        {
            if (mode != Mode.Cursor || hoverBusy || now - lastProbe < 150) return;
            lastProbe = now;
            hoverDirty = false;
            hoverBusy = true;
            double k = CssPerDesign() / Scale;
            string js = "window.__nomadNav&&window.__nomadNav.hit?window.__nomadNav.hit(" + (cursor.X * k).ToString("0.#", CultureInfo.InvariantCulture)
                + "," + (cursor.Y * k).ToString("0.#", CultureInfo.InvariantCulture) + "):false";
            view.EvaluateAsync(js, delegate(string r)
            {
                hoverBusy = false;
                SetHover(mode == Mode.Cursor && string.Equals(r, "true", StringComparison.OrdinalIgnoreCase));
            });
        }

        private void SetHover(bool on)
        {
            if (on == hoverOn) return;
            hoverOn = on;
            cursorHalo.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(on ? 1 : 0, TimeSpan.FromMilliseconds(on ? 140 : 200)));
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
            cursorHalo.Visibility = Visibility.Visible;
            if (mode == Mode.Focus) Script("show()");
        }

        private void HideWhenIdle(long now)
        {
            if (pointerHidden || !view.IsFullScreen || keyboard.IsOpen || mode == Mode.Menu || mode == Mode.Bar) return;
            if (now - lastInputAt < PointerIdleMs) return;
            pointerHidden = true;
            cursorSprite.Visibility = Visibility.Hidden;
            cursorHalo.Visibility = Visibility.Hidden;
            if (mode == Mode.Focus) Script("hide()");
        }

        // the cursor pushed against an edge scrolls the page under it (like a console browser), faster the harder it pushes
        private const double EdgeZone = 36;
        private void EdgePush(out double fx, out double fy, out string anchor)
        {
            fx = fy = 0;
            anchor = "";
            if (mode != Mode.Cursor) return;
            int dy = 0, dx = 0;
            if (cursor.Y >= page.ActualHeight - EdgeZone && pad.RightY < -0.25) dy = 1;          // stick down: scroll down
            else if (cursor.Y <= EdgeZone && pad.RightY > 0.25) dy = -1;
            if (cursor.X >= page.ActualWidth - EdgeZone && pad.RightX > 0.25) dx = 1;
            else if (cursor.X <= EdgeZone && pad.RightX < -0.25) dx = -1;
            if (dy == 0 && dx == 0) return;
            double push = Math.Max(Math.Abs(pad.RightX), Math.Abs(pad.RightY));
            double speed = 0.12 + push * push;   // viewport heights per second
            fx = dx * speed;
            fy = dy * speed;
            // scrolled a little inside the page, in the cursor's column / row: right at the edge it lands on a fixed header or bottom
            // bar, and on sites that scroll an inner panel (kick.com) a scroll there moves nothing
            double wx = cursor.X, wy = cursor.Y;
            if (dy != 0) wy = page.ActualHeight * (dy > 0 ? 0.7 : 0.3);
            if (dx != 0) wx = page.ActualWidth * (dx > 0 ? 0.7 : 0.3);
            double k = CssPerDesign() / Scale;
            anchor = (wx * k).ToString("0.#", CultureInfo.InvariantCulture) + "," + (wy * k).ToString("0.#", CultureInfo.InvariantCulture);
        }

        private bool phoneMovedCursor;   // the phone's trackpad already placed the cursor: the highlight must not move it back

        private void EnterCursor()
        {
            mode = Mode.Cursor;
            phoneMovedCursor = false;
            SetHover(false);
            if (cursor.X == 0 && cursor.Y == 0) cursor = new Point(page.ActualWidth / 2, page.ActualHeight / 2);
            // start the cursor where the highlight was
            view.EvaluateAsync("window.__nomadNav?window.__nomadNav.info():null", delegate(string json)
            {
                double x, y;
                if (mode == Mode.Cursor && !phoneMovedCursor && ParsePoint(json, out x, out y)) cursor = new Point(x * Scale / CssPerDesign(), y * Scale / CssPerDesign());
                PlaceCursor();
                hoverDirty = true;
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
            SetHover(false);
            if (nearCursor) Script("focusNear(" + (cursor.X * CssPerDesign() / Scale).ToString("0.#", CultureInfo.InvariantCulture) + "," + (cursor.Y * CssPerDesign() / Scale).ToString("0.#", CultureInfo.InvariantCulture) + ")");
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
                case "LeftShoulder":
                case "RightShoulder":
                    if (limiter.IsFirst(b)) { SwitchTab(TabStep(b)); EnterFocus(false); }   // quick tab switch
                    break;
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
            if (addrPill == null) return;
            bool home = IsHome || view.Address.Length == 0;
            addrPill.Update(home ? "" : view.Address, view.Title, home);
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

        // The Start menu is built in BrowserControl.Tools.cs (OpenMenu, ShowMainMenu, ShowPageTools); this draws a list of entries.
        private void ShowMenu(string title, List<MenuEntry> items, bool sub)
        {
            BarsShow();
            submenuOpen = sub;
            menuItems.Clear();
            menuItems.AddRange(items);
            if (menuTitleText != null) menuTitleText.Text = title;
            menuList.Children.Clear();
            foreach (var m in menuItems)
            {
                m.Box = Theme.Tile(m.Label, 24, MenuIcon(m.Label), Look.Pill);
                m.Box.Height = 60;
                m.Box.Padding = new Thickness(26, 0, 30, 1);
                m.Box.Margin = new Thickness(0, 3, 0, 3);
                Theme.AlignLeft(m.Box);
                Theme.SetFocusScale(m.Box, 1.03);   // (a row in a card grows a little less than a free pill)
                menuList.Children.Add(m.Box);
            }
            menuIndex = 0;
            mode = Mode.Menu;
            cursorLayer.Visibility = Visibility.Collapsed;
            Script("hide()");
            menuShade.Visibility = Visibility.Visible;
            menuCardFade.Appear();
            UpdateMenu();
            UpdateHints();
        }

        // Material icons for the menu's rows
        private static int MenuIcon(string label)
        {
            int tools = ToolsIcon(label);
            if (tools != 0) return tools;
            if (label.StartsWith("Add bookmark")) return 0xE867;      // bookmark_border
            if (label.StartsWith("Remove bookmark")) return 0xE866;   // bookmark
            if (label == "Zoom in") return 0xE8FF;
            if (label == "Zoom out") return 0xE900;
            if (label == "Reset zoom") return 0xE3BC;                 // crop_free (fit)
            if (label == "Settings") return 0xE8B8;                   // settings
            if (label == "New tab") return 0xE145;                    // add
            if (label == "Next tab") return 0xE8D2;                   // swap_horiz: switch tab
            if (label == "Close tab") return 0xE5C9;                  // cancel
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
                    if (item.Keep) { item.Run(); return true; }   // opens another menu
                    CloseMenu();
                    return item.Run();
                case "B": case "Start":
                    if (!limiter.IsFirst(b)) break;
                    if (submenuOpen && b == "B") ShowMainMenu(); else CloseMenu();
                    break;
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

        private int clearArmedAt;

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

        private static long Now() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds; }

        private static List<BrowserEntry> LoadList(string file)
        {
            try
            {
                lock (fileLock)
                {
                    if (File.Exists(file))
                    {
                        try
                        {
                            return new JavaScriptSerializer().Deserialize<List<BrowserEntry>>(File.ReadAllText(file, Encoding.UTF8)) ?? new List<BrowserEntry>();
                        }
                        catch
                        {
                            // unreadable: keep a copy before the next save replaces it with the (empty or default) list
                            try { File.Copy(file, file + ".bad", true); } catch { }
                            throw;
                        }
                    }
                }
            }
            catch (Exception ex) { logger.Error(ex, "SeleneBrowser: cannot read " + file); }
            return new List<BrowserEntry>();
        }

        // The newest serialized list per file that has not been written yet. Writes run on workers one at a time and always take the
        // newest pending text, so quick successive saves can never leave an older snapshot behind.
        private static readonly Dictionary<string, string> pendingSaves = new Dictionary<string, string>();

        // Serializes on the UI thread, writes on a worker so the UI never waits for the disk.
        private static void SaveList(string file, List<BrowserEntry> list)
        {
            string json = new JavaScriptSerializer().Serialize(list);
            lock (fileLock) pendingSaves[file] = json;
            Task.Run(delegate
            {
                try
                {
                    lock (fileLock)
                    {
                        string newest;
                        if (!pendingSaves.TryGetValue(file, out newest)) return;   // an earlier worker already wrote the newest text
                        pendingSaves.Remove(file);
                        Directory.CreateDirectory(Path.GetDirectoryName(file));
                        AtomicFile.WriteAllText(file, newest, Encoding.UTF8);
                    }
                }
                catch (Exception ex) { logger.Error(ex, "SeleneBrowser: cannot write " + file); }
            });
        }

        internal static List<BrowserEntry> DefaultBookmarks()
        {
            var names = new[] { "Google", "Wikipedia", "Reddit", "GitHub", "Twitch" };
            var urls = new[] { "https://www.google.com/", "https://www.wikipedia.org/", "https://www.reddit.com/", "https://github.com/", "https://www.twitch.tv/" };
            return names.Select((n, i) => new BrowserEntry { Title = n, Url = urls[i], Time = Now() }).ToList();
        }

        // ----- start page -----

        private void GoHome() { GoHome(active); }

        private void GoHome(BrowserTab tab)
        {
            // the themed launchpad (StartPage): rendered from the live palette now, so it follows the chosen theme
            if (tab != null && !tab.ViewDisposed)
            {
                string html = StartPage.Build(bookmarks, history, SearchAction);
                tab.View.Navigate("data:text/html;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(html)));
            }
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
            var stamp = ++toastStamp;
            if (toastFade != null) { if (toastFade.Shown) toastFade.Set(true, false); else toastFade.Appear(); }
            else toast.Visibility = Visibility.Visible;
            After(1800, delegate
            {
                if (stamp != toastStamp) return;
                if (toastFade != null) toastFade.Set(false, true); else toast.Visibility = Visibility.Collapsed;
            });
        }
        private int toastStamp;

        private void UpdateHints()
        {
            string h;
            if (keyboard.IsOpen) h = OnScreenKeyboard.Hints;
            else if (panelKind != PanelKind.None) h = PanelHints();
            else if (linkCard != null && linkCard.IsOpen) h = "B Close    X New address    Y Turn off";
            else if (mode == Mode.Menu) h = settings != null && settings.IsOpen ? "D-pad Select    A Switch on/off    B Back" : "D-pad Select    A Choose    B Close";
            else if (mode == Mode.Bar) h = "D-pad Select    A Open    LB/RB Switch tab    Y / B Back to page";
            else if (mode == Mode.Cursor) h = "R-stick Move    A Click    B Back    L-stick Scroll    D-pad Focus    Y Top bar    Start Menu";
            else if (findActive) h = "A Next match    Y Previous match    X Search again    B Done searching    D-pad Move    LT/RT Scroll";
            else h = "D-pad Move    A Select    B Back    X Reload    Y Top bar    LB/RB Tabs    L-stick Scroll    R-stick Cursor    L3/R3 Zoom    Start Menu";
            if (h == hints) return;
            hints = h;
            hintText.Show(h);
            if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("Hints"));
        }

        // ----- layout -----

        private UIElement BuildLayout()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var atmosphere = new Atmosphere();   // the theme's backdrop behind the bar and the hints (the page itself is a window on top)
            Grid.SetRowSpan(atmosphere, 3);
            root.Children.Add(atmosphere);

            // The floating header, like the home screen's: a bar of pills (back / forward / reload / home, the address, zoom, new / close
            // tab, the clock) and, under it while there is more than one tab, the tab row with the LB / RB plates at its ends.
            // 64 px side margins; every pill is 56 high; focus is the accent fill with its glow (Theme.Tile).
            var bar = new Grid();
            AddBarItem(bar, "Back", 0xE5C4, delegate { view.GoBack(); EnterFocus(false); });          // arrow_back
            AddBarItem(bar, "Forward", 0xE5C8, delegate { view.GoForward(); EnterFocus(false); });    // arrow_forward
            AddBarItem(bar, "Reload", 0xE5D5, delegate { view.Reload(); EnterFocus(false); });        // refresh
            AddBarItem(bar, "Home", 0xE88A, delegate { GoHome(); EnterFocus(false); });               // home
            addrPill = new AddressPill();
            addrPill.Pill.Margin = new Thickness(16, 0, 16, 0);
            AddBarItem(bar, addrPill.Pill, EditAddress, true);
            AddBarItem(bar, "Zoom -", 0xE900, delegate { ZoomBy(-ZoomStep); });   // zoom_out
            AddBarItem(bar, "Zoom +", 0xE8FF, delegate { ZoomBy(ZoomStep); });    // zoom_in
            AddBarItem(bar, "New tab", 0xE145, delegate { BrowserTab nt = NewTab(null, true); ActivateTab(nt); GoHome(nt); EnterFocus(false); });   // add
            AddBarItem(bar, "Close tab", 0xE5C9, delegate { CloseTab(active); EnterFocus(false); });               // cancel
            barItems[7].Margin = new Thickness(21, 0, 5, 0);   // the tab buttons sit apart from the zoom buttons
            // the clock at the right end: only while the bar is on screen and wide enough
            barClock = new ClockChip(26);
            barClock.Margin = new Thickness(24, 0, 4, 0);
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(barClock, bar.ColumnDefinitions.Count - 1);
            bar.Children.Add(barClock);
            bar.SizeChanged += delegate { barClock.Visibility = bar.ActualWidth >= 1500 ? Visibility.Visible : Visibility.Collapsed; };

            tabRow.Children.Add(Chrome.ShoulderBadge("LB"));
            tabRow.Children.Add(tabStrip);
            tabRow.Children.Add(Chrome.ShoulderBadge("RB"));
            var header = new StackPanel { Margin = new Thickness(64, 24, 64, 6), UseLayoutRounding = true };
            header.Children.Add(bar);
            header.Children.Add(tabRow);
            Grid.SetRow(header, 0);
            root.Children.Add(header);
            topBar = header;
            headerFade = new Fader(header, -16);

            cursorHalo = new System.Windows.Shapes.Ellipse
            {
                Width = 58, Height = 58,
                Stroke = Theme.Accent, StrokeThickness = 3,
                Fill = Theme.Tint("SeleneFocusColor", "#FFFFFFFF", 0.16),
                Opacity = 0, IsHitTestVisible = false
            };
            cursorLayer.Children.Add(cursorHalo);
            cursorSprite = new System.Windows.Shapes.Polygon
            {
                Points = new PointCollection { new Point(0, 0), new Point(0, 30), new Point(8, 23), new Point(14, 35), new Point(19, 32), new Point(13, 21), new Point(23, 21) },
                Fill = Theme.Ink,
                Stroke = Theme.ScrimA(0.9),
                StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round
                // No blur effect here: the browser poll moves this sprite every 16 ms, and a DropShadowEffect
                // would force a blurred re-rasterization on every tick. The stroke gives it enough contrast.
            };
            cursorLayer.Children.Add(cursorSprite);
            chrome.Children.Add(cursorLayer);

            // toast: a glass pill over the bottom of the page, just above the hints; fades and rises in
            Theme.Glass(toast, 26);
            toast.Padding = new Thickness(34, 12, 34, 14);
            toast.Margin = new Thickness(0, 0, 0, 28);
            toast.HorizontalAlignment = HorizontalAlignment.Center;
            toast.VerticalAlignment = VerticalAlignment.Bottom;
            toast.Visibility = Visibility.Collapsed;
            toast.IsHitTestVisible = false;
            toast.Child = toastText;
            toast.SizeChanged += delegate { toast.CornerRadius = new CornerRadius(Math.Max(0, toast.ActualHeight / 2)); };   // a pill: radius = height / 2
            toastFade = new Fader(toast, 10);
            chrome.Children.Add(toast);

            // passive pad-rest chip: above the page, never hit-tested or focused (the toast owns the bottom centre)
            restChip.View.Margin = new Thickness(0, 14, 0, 0);
            Panel.SetZIndex(restChip.View, 7);
            chrome.Children.Add(restChip.View);

            // Start menu: the page dimmed, a glass card with the browser's name over its rows (scaled down if the page is short)
            Theme.Glass(menuBox, 28);
            menuBox.Padding = new Thickness(22, 24, 22, 22);
            menuBox.MinWidth = 560;
            menuBox.HorizontalAlignment = HorizontalAlignment.Center;
            menuBox.VerticalAlignment = VerticalAlignment.Center;
            var menuStack = new StackPanel();
            var menuTitle = Theme.Title("Browser", 30);
            menuTitleText = menuTitle;
            menuTitle.Margin = new Thickness(26, 0, 26, 14);
            menuStack.Children.Add(menuTitle);
            menuStack.Children.Add(menuList);
            menuBox.Child = menuStack;
            menuCardFade = new Fader(menuBox, 10);
            menuShade.Background = Theme.ScrimA(0.62);
            menuShade.Visibility = Visibility.Collapsed;
            menuShade.Children.Add(new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(40, 24, 40, 24), Child = menuBox });
            chrome.Children.Add(menuShade);
            chrome.Children.Add(keyboard);

            Grid.SetRow(page, 1);
            root.Children.Add(page);

            // the hints: bottom, 64 px side margins, 21 pt labels
            hintText.Margin = new Thickness(64, 6, 64, 30);
            Grid.SetRow(hintText, 2);
            root.Children.Add(hintText);
            hintFade = new Fader(hintText, 14);
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

        // the shim with the theme's accent (the highlight ring) and scrim (its hairlines) in it
        private static string BuildShim()
        {
            return ShimSource.Replace("%ACCENT%", Theme.AccentHex).Replace("%SCRIM%", Theme.Css(Theme.ScrimColor, 0.55));
        }

        // the theme changed: new pages get the new colors in the shim, and the pages already open are told
        private void ApplyShimTheme()
        {
            shim = BuildShim();
            string js = "window.__nomadNav&&window.__nomadNav.setTheme&&window.__nomadNav.setTheme('" + Theme.AccentHex + "','" + Theme.Css(Theme.ScrimColor, 0.55) + "')";
            foreach (var t in tabs)
            {
                if (t.ViewDisposed) continue;
                try { t.View.ExecuteScript(js); }
                catch (Exception ex) { logger.Warn("SeleneBrowser: shim theme: " + ex.Message); }
            }
        }

        // Spatial-navigation / highlight shim, injected once per page load. %ACCENT% = theme accent color, %SCRIM% = the theme's scrim as rgba().
        private const string ShimSource = @"(function(){
if(window.__nomadNav) return;
var ACCENT='%ACCENT%', SCRIM='%SCRIM%';
var SEL='a[href],button,input:not([type=hidden]),textarea,select,summary,[role=button],[role=link],[role=tab],[role=menuitem],[role=checkbox],[role=radio],[role=switch],[role=option],[role=combobox],[tabindex],[onclick],[contenteditable=true],[contenteditable=""""],video,audio';
var NATIVE={A:1,BUTTON:1,INPUT:1,TEXTAREA:1,SELECT:1,SUMMARY:1};
var hl=null, cur=null, enabled=true;

// highlight box (never touches the page's own styles)
function ensureHl(){
 if(hl&&hl.isConnected) return hl;
 hl=document.createElement('div');
 hl.style.cssText='position:fixed;z-index:2147483647;pointer-events:none;box-sizing:border-box;border:3px solid '+ACCENT+';border-radius:12px;box-shadow:0 0 18px '+ACCENT+'8C,0 0 0 1px '+SCRIM+',inset 0 0 0 1px '+SCRIM+';display:none;margin:0;padding:0;transition:left .14s cubic-bezier(.2,.7,.3,1),top .14s cubic-bezier(.2,.7,.3,1),width .14s cubic-bezier(.2,.7,.3,1),height .14s cubic-bezier(.2,.7,.3,1)';
 document.documentElement.appendChild(hl);
 return hl;
}
// the theme changed: the ring takes the new accent at once
function setTheme(a,s){
 ACCENT=a; SCRIM=s;
 if(hl){ hl.style.borderColor=ACCENT; hl.style.boxShadow='0 0 18px '+ACCENT+'8C,0 0 0 1px '+SCRIM+',inset 0 0 0 1px '+SCRIM; }
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
function setCur(item){
 stopScroll(); cur={el:item.el}; reveal(item); place(); setTimeout(place,160);
 try{ if(window.__nomadFocus) window.__nomadFocus(item.el); }catch(e){}   // the start page grows the tile under the ring
}
// smooth D-pad scrolling: cancellable requestAnimationFrame tween (cubic ease-out)
var anim=null;
function stopScroll(){
 if(anim!==null){ try{ cancelAnimationFrame(anim); }catch(e){} anim=null; }
}
function tweenScroll(entries,ms){
 stopScroll();
 if(!entries.length) return;
 var t0=null, dur=ms||260;
 function frame(ts){
  if(t0===null) t0=ts;
  var p=Math.min(1,(ts-t0)/dur), e=1-Math.pow(1-p,3), i, en, n;
  for(i=0;i<entries.length;i++){
   en=entries[i]; n=en.node;
   try{
    if(en.x!==en.x0) n.scrollLeft=en.x0+(en.x-en.x0)*e;
    if(en.y!==en.y0) n.scrollTop=en.y0+(en.y-en.y0)*e;
   }catch(err){}
  }
  try{ place(); }catch(err){}
  if(p<1) anim=requestAnimationFrame(frame); else anim=null;
 }
 anim=requestAnimationFrame(frame);
}
function scrollChain(el){
 var out=[], doc=el.ownerDocument, n, w=doc.defaultView;
 function push(node){ if(node&&out.indexOf(node)<0) out.push(node); }
 function scrollable(node){
  if(!node) return false;
  var cs; try{ cs=node.ownerDocument.defaultView.getComputedStyle(node); }catch(e){ return false; }
  if(!cs) return false;
  var oy=cs.overflowY, ox=cs.overflowX;
  if(oy==='auto'||oy==='scroll'||oy==='overlay'||ox==='auto'||ox==='scroll'||ox==='overlay') return true;
  return (oy==='hidden'&&node.scrollHeight>node.clientHeight+1)||(ox==='hidden'&&node.scrollWidth>node.clientWidth+1);
 }
 for(n=el.parentElement;n;n=n.parentElement){ if(scrollable(n)) push(n); }
 var fw=doc.defaultView;
 while(fw&&fw!==window){
  var fe=null; try{ fe=fw.frameElement; }catch(e){}
  if(!fe) break;
  try{
   var fd=fe.ownerDocument;
   for(n=fe.parentElement;n;n=n.parentElement){ if(scrollable(n)) push(n); }
   var fse=fd&&fd.scrollingElement; if(fse) push(fse);
  }catch(e){}
  try{ fw=fw.parent; }catch(e){ break; }
 }
 var se=doc.scrollingElement; if(se) push(se);
 var td=w?w.document:null, tse=td&&td.scrollingElement;
 if(tse&&tse!==se) push(tse);
 return out;
}
function reveal(item){
 var vw=window.innerWidth, vh=window.innerHeight;
 if(item.l<0||item.t<0||item.l+item.w>vw||item.t+item.h>vh){
  var chain=scrollChain(item.el), saved=[], after=[], i, n;
  for(i=0;i<chain.length;i++){ n=chain[i]; saved.push({x:n.scrollLeft,y:n.scrollTop}); }
  var ok=false;
  try{ item.el.scrollIntoView({block:'center',inline:'center',behavior:'instant'}); ok=true; }catch(e){}
  if(ok){
   for(i=0;i<chain.length;i++){ n=chain[i]; after.push({x:n.scrollLeft,y:n.scrollTop}); }
   for(i=0;i<chain.length;i++){ n=chain[i]; try{ n.scrollLeft=saved[i].x; n.scrollTop=saved[i].y; }catch(e){} }
   var entries=[];
   for(i=0;i<chain.length;i++){
    n=chain[i];
    if(after[i].x!==saved[i].x||after[i].y!==saved[i].y) entries.push({node:n,x0:saved[i].x,x:after[i].x,y0:saved[i].y,y:after[i].y});
   }
   if(entries.length){ tweenScroll(entries,260); return; }
  }
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

// continuous scrolling (left stick, triggers, a cursor pushed against an edge): the host sends a speed and a place, and the page moves itself
// once per frame. Where there is no room left nothing moves and nothing is asked of the browser; it stops by itself if the host goes quiet.
var LS={fx:0,fy:0,ax:NaN,ay:NaN,run:false,raf:0,last:0,beat:0,rx:0,ry:0,mx:0,my:0,post:0,tg:{},blocked:{}};
function lsCan(n,vert,fwd){
 var pos=vert?n.scrollTop:n.scrollLeft, max=vert?n.scrollHeight-n.clientHeight:n.scrollWidth-n.clientWidth;
 return fwd?pos<max-1:pos>0;
}
function lsPoint(){
 if(LS.ax===LS.ax&&LS.ay===LS.ay) return [LS.ax,LS.ay];
 if(cur&&cur.el.isConnected){ var r=absRect(cur); return [Math.min(Math.max(r.l+r.w/2,1),window.innerWidth-2),Math.min(Math.max(r.t+r.h/2,1),window.innerHeight-2)]; }
 return [window.innerWidth/2,window.innerHeight/2];
}
// the nearest thing under the point that can still move that way (the page itself last), or null
function lsTarget(vert,fwd){
 var p=lsPoint(), el=null, n;
 try{ el=document.elementFromPoint(p[0],p[1]); }catch(e){}
 for(n=el;n&&n!==document.body&&n!==document.documentElement;n=n.parentElement){
  var cs=getComputedStyle(n), ov=vert?cs.overflowY:cs.overflowX;
  if((ov==='auto'||ov==='scroll'||ov==='overlay')&&lsCan(n,vert,fwd)) return n;
 }
 var se=document.scrollingElement;
 if(!se||!lsCan(se,vert,fwd)) return null;
 var hs=getComputedStyle(document.documentElement), bs=document.body?getComputedStyle(document.body):hs;
 var vo=vert?(hs.overflowY==='visible'?bs.overflowY:hs.overflowY):(hs.overflowX==='visible'?bs.overflowX:hs.overflowX);
 return (vo==='hidden'||vo==='clip')?null:se;
}
function lsMove(vert,d){
 var fwd=d>0, key=(vert?'v':'h')+(fwd?'f':'b'), now=performance.now();
 if(LS.blocked[key]&&now<LS.blocked[key]) return;
 var n=LS.tg[key];
 if(!n||!n.isConnected||!lsCan(n,vert,fwd)){ n=lsTarget(vert,fwd); LS.tg[key]=n; }
 if(!n){ LS.blocked[key]=now+250; return; }
 var before=vert?n.scrollTop:n.scrollLeft;
 try{ n.scrollBy({left:vert?0:d,top:vert?d:0,behavior:'instant'}); }catch(e){ if(vert) n.scrollTop+=d; else n.scrollLeft+=d; }
 var moved=(vert?n.scrollTop:n.scrollLeft)-before;
 if(moved===0){ LS.tg[key]=null; LS.blocked[key]=now+120; return; }
 if(vert) LS.my+=moved; else LS.mx+=moved;
}
function lsTell(force){
 var now=performance.now();
 if(!force&&now-LS.post<120) return;
 if(LS.mx===0&&LS.my===0) return;
 LS.post=now;
 try{ window.chrome.webview.postMessage('lfscroll:'+Math.round(LS.mx)+':'+Math.round(LS.my)); }catch(e){}
 LS.mx=0; LS.my=0;
}
function lsStop(){
 LS.run=false; LS.fx=0; LS.fy=0;
 if(LS.raf){ try{ cancelAnimationFrame(LS.raf); }catch(e){} LS.raf=0; }
 lsTell(true); LS.tg={}; LS.blocked={};
}
function lsFrame(ts){
 if(!LS.run) return;
 if(performance.now()-LS.beat>450){ lsStop(); return; }
 var dt=LS.last?Math.min(0.05,(ts-LS.last)/1000):0.016; LS.last=ts;
 var dpr=window.devicePixelRatio||1, vh=window.innerHeight;
 var dx=LS.fx*vh*dt+LS.rx, dy=LS.fy*vh*dt+LS.ry;
 var ix=Math.round(dx*dpr)/dpr, iy=Math.round(dy*dpr)/dpr;
 LS.rx=dx-ix; LS.ry=dy-iy;
 if(iy!==0) lsMove(true,iy);
 if(ix!==0) lsMove(false,ix);
 lsTell(false);
 LS.raf=requestAnimationFrame(lsFrame);
}

window.__nomadNav={
 // scroll at a speed (viewport heights per second, + = down / right) at a point (css px) or at the highlight; 0,0 stops
 scroll:function(fx,fy,ax,ay){
  LS.fx=+fx||0; LS.fy=+fy||0;
  LS.ax=(ax===undefined||ax===null)?NaN:+ax; LS.ay=(ay===undefined||ay===null)?NaN:+ay;
  LS.beat=performance.now();
  if(LS.fx===0&&LS.fy===0){ if(LS.run) lsStop(); return; }
  if(!LS.run){ LS.run=true; LS.last=0; LS.rx=0; LS.ry=0; LS.tg={}; LS.blocked={}; LS.raf=requestAnimationFrame(lsFrame); }
 },
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
  var x0=sc.scrollLeft, y0=sc.scrollTop;
  var maxX=Math.max(0,sc.scrollWidth-sc.clientWidth), maxY=Math.max(0,sc.scrollHeight-sc.clientHeight);
  var dx=d==='right'?amt:(d==='left'?-amt:0), dy=d==='down'?amt:(d==='up'?-amt:0);
  var x=Math.min(maxX,Math.max(0,x0+dx)), y=Math.min(maxY,Math.max(0,y0+dy));
  tweenScroll([{node:sc,x0:x0,x:x,y0:y0,y:y}],260);
  place(); setTimeout(place,160);
  return 'scrolled';
 },
 setTheme:setTheme,
 // is something clickable under this viewport point (css px)? the cursor's halo asks
 hit:function(x,y){
  try{
   var e=document.elementFromPoint(x,y);
   while(e&&e.closest){
    var t=e.closest(SEL);
    if(!t) return false;
    if(!(t.disabled||(t.getAttribute('tabindex')==='-1'&&!NATIVE[t.tagName]))) return true;
    e=t.parentElement;
   }
   return false;
  }catch(z){ return false; }
 },
 hide:function(){ enabled=false; place(); },
 show:function(){ enabled=true; place(); },
 // center of the highlighted element in viewport css px, clamped to the viewport; null if nothing is highlighted
 info:function(){
  if(!cur||!cur.el.isConnected) return null;
  var r=absRect(cur);
  return JSON.stringify({x:Math.min(Math.max(r.l+r.w/2,1),window.innerWidth-2),y:Math.min(Math.max(r.t+r.h/2,1),window.innerHeight-2)});
 },
 // the link under the highlight (no arguments) or under a point (css px): its address and words, or null
 link:function(x,y){
  var el=null;
  if(x===undefined){ if(cur&&cur.el&&cur.el.isConnected) el=cur.el; } else { el=document.elementFromPoint(x,y); }
  var a=el&&el.closest?el.closest('a[href]'):null;
  if(!a||!a.href||!/^https?:/i.test(a.href)) return null;
  return JSON.stringify({href:a.href,text:(a.innerText||a.getAttribute('aria-label')||a.title||'').trim().slice(0,80)});
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
  var more=false;
  try{
   if(!multi && ty!=='search'){
    var all=document.querySelectorAll('input,textarea'), seen=false;
    for(var i=0;i<all.length;i++){
     var x=all[i]; if(x===e){seen=true;continue;} if(!seen) continue;
     if(e.form && x.form!==e.form) continue;
     if(x.tagName==='INPUT' && !/^(|text|email|tel|password|number|url)$/.test((x.type||'').toLowerCase())) continue;
     if(x.disabled||x.readOnly) continue;
     var r=x.getBoundingClientRect(); if(r.width<2||r.height<2) continue;
     var st=getComputedStyle(x); if(st.visibility==='hidden'||st.display==='none') continue;
     more=true; break;
    }
   }
  }catch(z){}
  return JSON.stringify({value:v||'',multi:multi,more:more,password:ty==='password',id:e.id||'',label:e.getAttribute('aria-label')||e.placeholder||e.name||''});
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
window.addEventListener('wheel',stopScroll,true);
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
                DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lanternfall");
            }
            // Chromium flags cannot be added here: Playnite initializes CEF before plugins load, so
            // trusted input events from CefView replace the old autoplay-policy switch.
            logger.Info("SeleneBrowser: plugin loaded");
        }

        public override void OnApplicationStarted(Playnite.SDK.Events.OnApplicationStartedEventArgs args)
        {
            CardInfo.Refresh();   // the numbers on the home screen's Browser and YouTube cards
            CardInfo.WatchFeed();
            try { if (PlayniteApi.ApplicationInfo.Mode == ApplicationMode.Fullscreen) RemoteService.AutoStart(); }
            catch (Exception ex) { logger.Warn("SeleneBrowser: phone remote start: " + ex.Message); }
        }

        public override void OnApplicationStopped(Playnite.SDK.Events.OnApplicationStoppedEventArgs args)
        {
            try { RemoteService.Shutdown(); }
            catch (Exception ex) { logger.Warn("SeleneBrowser: phone link shutdown: " + ex.Message); }
        }

        public override Control GetGameViewControl(GetGameViewControlArgs args)
        {
            if (args.Name == "BrowserSurface") return new BrowserControl();
            if (args.Name == "YouTubeSurface") return new YouTubeControl();
            return null;
        }
    }
}
