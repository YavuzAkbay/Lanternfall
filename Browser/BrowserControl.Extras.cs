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
        private int barsHintStamp;
        private static bool barsHintSeen;

        // called once from the constructor, after the layout exists
        private void InitExtras()
        {
            settings = new BrowserSettingsPanel(limiter, delegate { return PageViews.IsEdge(view); });
            page.Children.Add(settings);

            // the small reminder shown for a moment when the bars step aside
            barsHintBox = new Border();
            Theme.Glass(barsHintBox, 20);
            barsHintBox.Padding = new Thickness(12, 3, 16, 3);
            barsHintBox.Margin = new Thickness(0, 0, 22, 18);
            barsHintBox.HorizontalAlignment = HorizontalAlignment.Right;
            barsHintBox.VerticalAlignment = VerticalAlignment.Bottom;
            barsHintBox.Visibility = Visibility.Collapsed;
            barsHintBox.IsHitTestVisible = false;
            var hint = new HintBar(16, HorizontalAlignment.Right);
            hint.Show("Y / Back Show bars");
            barsHintBox.Child = hint;
            page.Children.Add(barsHintBox);

            bars = new BarAutoHide(OnBarsApply, CanHideBars, ReadScrollY);
            bars.Enabled = BrowserSettings.AutoHideBars;
            Loaded += delegate { BrowserSettings.Changed += OnSettingsChanged; OnSettingsChanged(); };
            Unloaded += delegate { BrowserSettings.Changed -= OnSettingsChanged; settings.Close(); };
        }

        private void OnSettingsChanged()
        {
            if (bars != null) bars.Enabled = BrowserSettings.AutoHideBars;
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
            var vis = fs || aside ? Visibility.Collapsed : Visibility.Visible;
            if (topBar != null) topBar.Visibility = vis;
            hintText.Visibility = vis;
            if (!aside && barsHintBox != null) { barsHintBox.Visibility = Visibility.Collapsed; barsHintStamp++; }
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
            barsHintBox.Visibility = Visibility.Visible;
            int stamp = ++barsHintStamp;
            After(ms, delegate { if (stamp == barsHintStamp) barsHintBox.Visibility = Visibility.Collapsed; });
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
