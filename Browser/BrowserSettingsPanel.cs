// BrowserSettingsPanel.cs - the browser's Settings card (Start menu > Settings): switch rows with a clear ON / OFF chip and a
// live ad-blocking status line. Built from the Theme helpers like the Start menu; driven by the pad through HandlePad.
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace SeleneBrowser
{
    internal sealed class BrowserSettingsPanel : Grid
    {
        private sealed class Row
        {
            public Border Tile;
            public Border Chip;
            public Func<string> State;      // "ON" / "OFF" / "N/A"
            public Action Toggle;
        }

        private readonly RepeatLimiter limiter;
        private readonly Func<bool> adBlockAvailable;
        private readonly List<Row> rows = new List<Row>();
        private readonly TextBlock adStatus = Theme.Text("", 18, Theme.Ink60);
        private readonly TextBlock adNote = Theme.Text("", 18, Theme.Ink60);
        private readonly DispatcherTimer refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500 ) };
        private int index;

        public bool IsOpen { get { return Visibility == Visibility.Visible; } }

        /// <param name="adBlockAvailable">True when the page engine can block ads (Edge WebView2); false on the CEF fallback.</param>
        public BrowserSettingsPanel(RepeatLimiter limiter, Func<bool> adBlockAvailable)
        {
            this.limiter = limiter;
            this.adBlockAvailable = adBlockAvailable;
            Background = Theme.ScrimA(0.62);
            Visibility = Visibility.Collapsed;

            var card = new Border();
            Theme.Glass(card, 28);
            card.Padding = new Thickness(22, 24, 22, 22);
            card.MinWidth = 600;
            card.MaxWidth = 760;
            card.HorizontalAlignment = HorizontalAlignment.Center;
            card.VerticalAlignment = VerticalAlignment.Center;
            var stack = new StackPanel();
            var title = Theme.Title("Browser settings", 30);
            title.Margin = new Thickness(24, 0, 24, 14);
            stack.Children.Add(title);

            AddRow(stack, "Ad blocking", 0xE14B,   // block
                delegate { return adBlockAvailable() ? (BrowserSettings.AdBlockEnabled ? "ON" : "OFF") : "N/A"; },
                delegate { if (adBlockAvailable()) BrowserSettings.AdBlockEnabled = !BrowserSettings.AdBlockEnabled; });
            adStatus.TextWrapping = TextWrapping.Wrap;
            adStatus.Margin = new Thickness(26, 0, 26, 2);
            stack.Children.Add(adStatus);
            adNote.TextWrapping = TextWrapping.Wrap;
            adNote.Margin = new Thickness(26, 0, 26, 12);
            stack.Children.Add(adNote);

            AddRow(stack, "Auto-hide bars", 0xE5D0,   // fullscreen
                delegate { return BrowserSettings.AutoHideBars ? "ON" : "OFF"; },
                delegate { BrowserSettings.AutoHideBars = !BrowserSettings.AutoHideBars; });
            var barsNote = Theme.Text("Hides the address bar and button hints while you scroll a page. Y brings them back.", 18, Theme.Ink60);
            barsNote.TextWrapping = TextWrapping.Wrap;
            barsNote.Margin = new Thickness(26, 0, 26, 2);
            stack.Children.Add(barsNote);

            card.Child = stack;
            Children.Add(card);
            refresh.Tick += delegate { Refresh(); };
        }

        private void AddRow(StackPanel host, string label, int icon, Func<string> state, Action toggle)
        {
            var tile = Theme.Tile(label, 24, icon, Look.Ghost);
            tile.Padding = new Thickness(24, 11, 130, 12);
            tile.Margin = new Thickness(0, 3, 0, 3);
            Theme.AlignLeft(tile);
            Theme.SetFocusScale(tile, 1.02);   // (the chip beside the label does not scale with it)

            var chip = Theme.Chip("OFF", Theme.InkA(0.16), Theme.Ink60, 20);
            chip.MinWidth = 64;
            chip.Padding = new Thickness(14, 3, 14, 4);
            chip.HorizontalAlignment = HorizontalAlignment.Right;
            chip.VerticalAlignment = VerticalAlignment.Center;
            chip.Margin = new Thickness(0, 0, 26, 0);
            ((TextBlock)chip.Child).HorizontalAlignment = HorizontalAlignment.Center;
            Border pill = chip;
            chip.SizeChanged += delegate { pill.CornerRadius = new CornerRadius(Math.Max(0, pill.ActualHeight / 2)); };   // a pill: radius = height / 2

            Panel.SetZIndex(chip, 5);   // above the tile (a focused tile is raised to 1)
            var g = new Grid();
            g.Children.Add(tile);
            g.Children.Add(chip);
            host.Children.Add(g);
            rows.Add(new Row { Tile = tile, Chip = chip, State = state, Toggle = toggle });
        }

        public void Open()
        {
            index = 0;
            Visibility = Visibility.Visible;
            Refresh();
            refresh.Start();
        }

        public void Close()
        {
            refresh.Stop();
            Visibility = Visibility.Collapsed;
        }

        /// <summary>Pad input while open. Returns true when the panel asks to be closed.</summary>
        public bool HandlePad(string b)
        {
            switch (b)
            {
                case "DPadUp": if (limiter.Allow(b)) { index = (index + rows.Count - 1) % rows.Count; Refresh(); } break;
                case "DPadDown": if (limiter.Allow(b)) { index = (index + 1) % rows.Count; Refresh(); } break;
                case "A": if (limiter.IsFirst(b)) { rows[index].Toggle(); Refresh(); } break;
                case "B": case "Start": return limiter.IsFirst(b);
            }
            return false;
        }

        private void Refresh()
        {
            for (int i = 0; i < rows.Count; i++)
            {
                Row r = rows[i];
                Theme.SetFocused(r.Tile, i == index);
                string s = r.State();
                bool on = s == "ON";
                var t = (TextBlock)r.Chip.Child;
                if (t.Text != s) t.Text = s;
                if (i == index)   // on the focused row (filled with the accent) the chip is inverted so it stays readable
                {
                    r.Chip.Background = on ? Theme.OnAccent : Theme.Tint("SeleneOnFocusColor", "#FF111111", 0.16);
                    t.Foreground = on ? Theme.Accent : Theme.OnAccent;
                }
                else
                {
                    r.Chip.Background = on ? Theme.InkA(0.92) : Theme.InkA(0.16);
                    t.Foreground = on ? Theme.Tint("SeleneScrimColor", "#FF000000", 1) : Theme.Ink60;
                }
            }
            string status, note = "";
            if (!adBlockAvailable())
                status = "Ad blocking is not available on this browser engine (CEF). It needs the Edge WebView2 runtime.";
            else if (!BrowserSettings.AdBlockEnabled)
                status = "Ad blocking is off";
            else
                status = "Blocking ads: " + AdBlock.BlockedCount + " blocked this session (" + AdBlock.PageBlockedCount + " on this page)";
            if (adBlockAvailable() && BrowserSettings.AdBlockEnabled != BrowserSettings.AdBlockAtStartup)
                note = BrowserSettings.AdBlockEnabled
                    ? "Restart Playnite to also load uBlock Origin Lite."
                    : "Restart Playnite to fully apply: uBlock Origin Lite keeps filtering until then.";
            if (adStatus.Text != status) adStatus.Text = status;
            if (adNote.Text != note) adNote.Text = note;
            adNote.Visibility = note.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
