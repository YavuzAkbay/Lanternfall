// BrowserControl.Extras.cs - the browser's settings screen and the auto-hiding bars (see BrowserSettings, BarAutoHide).
// Kept out of SeleneBrowser.cs so the main file only carries a few one-line hooks into this one.
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace SeleneBrowser
{
    public partial class BrowserControl
    {
        private BrowserSettingsPanel settings;
        private BarAutoHide bars;
        private Border barsHintBox;
        private Fader barsHintFade;
        private int barsHintStamp;
        private static bool barsHintSeen;
        // in-page chrome lives in a separate transparent, click-through window over the windowed WebView2's HWND
        // (WPF content in the same window would be hidden by airspace). SeleneBrowser.cs puts the rest of the chrome in here.
        private PlayerOverlayWindow chromeOverlay;
        private readonly Grid chrome = new Grid();

        // called once from the constructor, after the layout exists
        private void InitExtras()
        {
            settings = new BrowserSettingsPanel(limiter, delegate { return PageViews.IsEdge(view); });
            chrome.Children.Add(settings);
            linkCard = new PhoneLinkCard();
            linkCard.Closed += delegate { EnterFocus(false); UpdateHints(); };
            chrome.Children.Add(linkCard);

            // the small reminder shown for a moment when the bars step aside
            barsHintBox = new Border();
            Theme.Glass(barsHintBox, 24);
            barsHintBox.Padding = new Thickness(16, 6, 24, 6);
            barsHintBox.Margin = new Thickness(0, 0, 64, 40);
            barsHintBox.HorizontalAlignment = HorizontalAlignment.Right;
            barsHintBox.VerticalAlignment = VerticalAlignment.Bottom;
            barsHintBox.Visibility = Visibility.Collapsed;
            barsHintBox.IsHitTestVisible = false;
            barsHintBox.SizeChanged += delegate { barsHintBox.CornerRadius = new CornerRadius(Math.Max(0, barsHintBox.ActualHeight / 2)); };   // a pill
            var hint = new HintBar(21, HorizontalAlignment.Right);
            hint.Show("Y / Back Show bars");
            barsHintBox.Child = hint;
            barsHintFade = new Fader(barsHintBox, 10);
            chrome.Children.Add(barsHintBox);

            bars = new BarAutoHide(OnBarsApply, CanHideBars, ReadScrollY);
            bars.Enabled = BrowserSettings.AutoHideBars;
            Loaded += delegate { BrowserSettings.Changed += OnSettingsChanged; OnSettingsChanged(); Theme.Changed += OnThemeChanged; };
            Unloaded += delegate
            {
                BrowserSettings.Changed -= OnSettingsChanged;
                Theme.Changed -= OnThemeChanged;
                settings.Close();
                if (chromeOverlay != null) chromeOverlay.Close();
            };
        }

        // SeleneBrowser.cs calls this after BuildLayout(), once the page (the WebView2 host) exists to anchor to.
        // PlayerOverlayWindow sizes the overlay in screen DIPs; the chrome is laid out in the theme's design
        // units, so a Viewbox maps the anchor's design rectangle onto the overlay exactly (same as the player).
        public void InitChromeOverlay(FrameworkElement anchor)
        {
            var design = new Grid();
            design.Children.Add(chrome);
            var box = new Viewbox { Stretch = System.Windows.Media.Stretch.Fill, Child = design, IsHitTestVisible = false };
            chromeOverlay = new PlayerOverlayWindow(anchor, box);
            anchor.LayoutUpdated += delegate
            {
                if (anchor.ActualWidth <= 0) return;
                if (Math.Abs(design.Width - anchor.ActualWidth) < 0.5 && Math.Abs(design.Height - anchor.ActualHeight) < 0.5) return;
                design.Width = anchor.ActualWidth;
                design.Height = anchor.ActualHeight;
            };
        }

        // the user picked another Lanternfall theme: what is not WPF follows - the page highlight ring, and the start pages
        private void OnThemeChanged()
        {
            try
            {
                ApplyShimTheme();
                foreach (var t in tabs)
                {
                    if (t.ViewDisposed || !IsHomeTab(t)) continue;
                    GoHome(t);   // re-rendered from the new palette (an open site keeps its own colors)
                }
            }
            catch (Exception ex) { logger.Error(ex, "SeleneBrowser: theme change"); }
        }

        private void OnSettingsChanged()
        {
            if (bars != null) bars.Enabled = BrowserSettings.AutoHideBars;
            if (tabs != null) ApplyBaseScaleAll();   // the page scale setting
        }

        // ----- settings screen (a Start menu entry; it lives in Mode.Menu like the menu itself) -----

        private void OpenSettings()
        {
            BarsShow();
            mode = Mode.Menu;
            cursorLayer.Visibility = Visibility.Collapsed;
            Script("hide()");
            settings.Open();
            UpdateHints();
        }

        private PhoneLinkCard linkCard;

        private void OpenLinkCard()
        {
            BarsShow();
            mode = Mode.Menu;
            cursorLayer.Visibility = Visibility.Collapsed;
            Script("hide()");
            linkCard.Open();
            UpdateHints();
        }

        private void CloseSettings()
        {
            settings.Close();
            EnterFocus(false);
        }

        private bool HandleSettings(string b)
        {
            if (settings.HandlePad(b)) CloseSettings();
            return true;
        }

        // ----- auto-hide bars -----

        // top bar and hint strip: hidden for fullscreen video, or while the auto-hide has stepped aside
        private void ApplyBars()
        {
            bool fs = view.IsFullScreen;
            bool aside = bars != null && bars.Hidden;
            // the header (bar + tab row) and the hints fade and slide away, then give the page their room; fullscreen video cuts at once
            bool show = !(fs || aside);
            if (headerFade != null) headerFade.Set(show, !fs);
            if (hintFade != null) hintFade.Set(show, !fs);
            if (!aside && barsHintBox != null) { barsHintFade.Set(false, false); barsHintStamp++; }
        }

        private void OnBarsApply(bool hide)
        {
            ApplyBars();
            if (hide && !view.IsFullScreen) FlashBarsHint();
        }

        // how to get the bars back; longer the very first time
        private void FlashBarsHint()
        {
            int ms = barsHintSeen ? 2500 : 6000;
            barsHintSeen = true;
            barsHintFade.Appear();
            int stamp = ++barsHintStamp;
            After(ms, delegate { if (stamp == barsHintStamp) barsHintFade.Set(false, true); });
        }

        private bool CanHideBars()
        {
            return shown && !keyboard.IsOpen && (mode == Mode.Focus || mode == Mode.Cursor) && !view.IsFullScreen;
        }

        // restore the bars on purpose (they stay until the user scrolls again)
        private void BarsShow() { if (bars != null) bars.Show(true); }
        private void BarsScrolled(int dir, int amount) { if (bars != null) bars.Scrolled(dir, amount); }
        private void BarsMoved(double seconds) { if (bars != null) bars.Moved(seconds); }
        private void BarsNewPage(string url) { if (bars != null) bars.NewPage(url); }
        private void BarsStart() { if (bars == null) return; bars.Start(); bars.Show(true); }
        private void BarsStop() { if (bars != null) bars.Stop(); }

        private void ReadScrollY(Action<double> done)
        {
            view.EvaluateAsync("Math.max(window.pageYOffset||0,document.scrollingElement?document.scrollingElement.scrollTop:0)", delegate(string r)
            {
                double y;
                if (r == null || !double.TryParse(r.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out y)) y = double.NaN;
                try { done(y); }
                catch (Exception ex) { logger.Error(ex, "SeleneBrowser: scroll probe"); }
            });
        }
    }
}
