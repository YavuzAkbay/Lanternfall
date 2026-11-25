// BrowserSettingsPanel.cs - the browser's Settings card (Start menu > Settings): switch rows with a clear ON / OFF switch and a
// live ad-blocking status line. Built from the Theme helpers like the Start menu (a glass card, pill rows with icons, the scrim behind);
// driven by the pad through HandlePad.
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Threading.Tasks;

namespace SeleneBrowser
{
    internal sealed class BrowserSettingsPanel : Grid
    {
        private sealed class Row
        {
            public Border Tile;
            public StackPanel Switch;       // "ON" / "OFF" word and the track with its knob
            public TextBlock Word;
            public Border Track;
            public Ellipse Knob;
            public Func<string> State;      // "ON" / "OFF" / "N/A"
            public Action Toggle;
            public bool Choice;             // shows a value word, no switch
        }

        private readonly RepeatLimiter limiter;
        private readonly Func<bool> adBlockAvailable;
        private readonly List<Row> rows = new List<Row>();
        private readonly TextBlock adStatus = Theme.Text("", 20, Theme.Ink60);
        private readonly TextBlock adNote = Theme.Text("", 20, Theme.Ink60);
        private readonly TextBlock phoneNote = Theme.Text("", 20, Theme.Ink60);
        private volatile bool firewallBusy;
        private volatile string firewallMessage = "";
        private string phoneNoteExtra = "";
        private readonly DispatcherTimer refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        private readonly Border card = new Border();
        private readonly Fader cardFade;
        private int index;

        public bool IsOpen { get { return Visibility == Visibility.Visible; } }

        /// <param name="adBlockAvailable">True when the page engine can block ads (Edge WebView2); false on the CEF fallback.</param>
        public BrowserSettingsPanel(RepeatLimiter limiter, Func<bool> adBlockAvailable)
        {
            this.limiter = limiter;
            this.adBlockAvailable = adBlockAvailable;
            Background = Theme.ScrimA(0.62);
            Visibility = Visibility.Collapsed;

            Theme.Glass(card, 28);
            card.Padding = new Thickness(24, 26, 24, 22);
            card.MinWidth = 700;
            card.MaxWidth = 820;
            card.HorizontalAlignment = HorizontalAlignment.Center;
            card.VerticalAlignment = VerticalAlignment.Center;
            var stack = new StackPanel();
            var title = Theme.Title("Browser settings", 30);
            title.Margin = new Thickness(26, 0, 26, 14);
            stack.Children.Add(title);

            AddRow(stack, "Ad blocking", 0xE14B,   // block
                delegate { return adBlockAvailable() ? (BrowserSettings.AdBlockEnabled ? "ON" : "OFF") : "N/A"; },
                delegate { if (adBlockAvailable()) BrowserSettings.AdBlockEnabled = !BrowserSettings.AdBlockEnabled; });
            adStatus.TextWrapping = TextWrapping.Wrap;
            adStatus.Margin = new Thickness(28, 2, 28, 2);
            stack.Children.Add(adStatus);
            adNote.TextWrapping = TextWrapping.Wrap;
            adNote.Margin = new Thickness(28, 0, 28, 12);
            stack.Children.Add(adNote);

            AddRow(stack, "Auto-hide bars", 0xE5D0,   // fullscreen
                delegate { return BrowserSettings.AutoHideBars ? "ON" : "OFF"; },
                delegate { BrowserSettings.AutoHideBars = !BrowserSettings.AutoHideBars; });
            var barsNote = Theme.Text("Hides the address bar and button hints while you scroll a page. Y brings them back.", 20, Theme.Ink60);
            barsNote.TextWrapping = TextWrapping.Wrap;
            barsNote.Margin = new Thickness(28, 2, 28, 2);
            stack.Children.Add(barsNote);

            AddRow(stack, "Page size", 0xE8FF,   // zoom_in
                delegate { return SizeLabel(); },
                delegate { NextSize(); }, true);
            var sizeNote = Theme.Text("Auto fits pages to your screen, so a 4K TV reads like a 1080p one. Too small from the couch? Pick Auto larger. The percentages are fixed sizes. L3 / R3 still zoom on top.", 20, Theme.Ink60);
            sizeNote.TextWrapping = TextWrapping.Wrap;
            sizeNote.Margin = new Thickness(28, 2, 28, 2);
            stack.Children.Add(sizeNote);

            AddRow(stack, "Downloads", 0xE2C4,   // file_download
                delegate { return BrowserSettings.DownloadsAllowed ? "ON" : "OFF"; },
                delegate { BrowserSettings.DownloadsAllowed = !BrowserSettings.DownloadsAllowed; });
            var downloadsNote = Theme.Text("A file is saved to your Downloads folder only after you say yes on the screen.", 20, Theme.Ink60);
            downloadsNote.TextWrapping = TextWrapping.Wrap;
            downloadsNote.Margin = new Thickness(28, 2, 28, 2);
            stack.Children.Add(downloadsNote);

            AddRow(stack, "Phone keyboard", 0xE32C,   // smartphone
                delegate { return BrowserSettings.PhoneKeyboard ? "ON" : "OFF"; },
                delegate { BrowserSettings.PhoneKeyboard = !BrowserSettings.PhoneKeyboard; });
            AddRow(stack, "Phone keyboard for passwords", 0xE897,   // lock
                delegate { return BrowserSettings.PhonePasswords ? "ON" : "OFF"; },
                delegate { BrowserSettings.PhonePasswords = !BrowserSettings.PhonePasswords; });
            AddRow(stack, "Phone link", 0xE335,   // cast_connected
                delegate { return BrowserSettings.PhoneRemote ? "ON" : "OFF"; },
                delegate { if (BrowserSettings.PhoneRemote) RemoteService.Disable(); else RemoteService.Enable(); });
            AddRow(stack, "Reset phone link", 0xE5D5,   // refresh
                delegate { return "Reset"; },
                delegate { RemoteService.Reset(); phoneNoteExtra = "Made a new address. Phones that had the old one are cut off; scan the code again."; }, true);
            AddRow(stack, "Let phones reach this PC", 0xE63E,   // wifi
                delegate { return FirewallWord(); },
                delegate { FirewallPress(); }, true);
            phoneNote.TextWrapping = TextWrapping.Wrap;
            phoneNote.Margin = new Thickness(28, 2, 28, 2);
            stack.Children.Add(phoneNote);

            card.Child = stack;
            cardFade = new Fader(card, 10);
            // (scaled down, never up, when the page is short)
            Children.Add(new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(40, 24, 40, 24), Child = card });
            refresh.Tick += delegate { Refresh(); };
        }

        // ----- phone keyboard: the one-time Windows Firewall step -----

        private string FirewallWord()
        {
            if (firewallBusy) return "Working...";
            switch (PhoneFirewall.Last)
            {
                case FirewallState.Open: return "Allowed";
                case FirewallState.Missing: return "Set up";
                case FirewallState.Blocked: return "Fix";
            }
            return "Check";
        }

        // A: set it up (Windows' own admin box appears on the desktop: answer it with a mouse or keyboard), or just look again
        private void FirewallPress()
        {
            if (firewallBusy) return;
            firewallBusy = true;
            FirewallState was = PhoneFirewall.Last;
            if (was == FirewallState.Open || was == FirewallState.Unknown)
            {
                firewallMessage = "Looking at Windows Firewall...";
                Task.Run(delegate
                {
                    FirewallState now = PhoneFirewall.Check();
                    firewallMessage = now == FirewallState.Open ? "Your phone can reach this PC."
                        : now == FirewallState.Unknown ? "Windows would not say. Try the phone keyboard: Windows may ask once."
                        : "Not allowed yet. Press A again to set it up.";
                    firewallBusy = false;
                });
                return;
            }
            firewallMessage = "Windows will ask for permission on the desktop. Answer it with a mouse or keyboard.";
            Task.Run(delegate
            {
                string error;
                bool ok = PhoneFirewall.Setup(out error);
                firewallMessage = ok ? "Done. Your phone can reach this PC." : (error ?? "It did not work.");
                firewallBusy = false;
            });
        }

        // The page size choices, in the order A cycles them: { fixed scale (0 = Auto), Auto's boost }. Auto follows the screen; the
        // "larger" / "smaller" steps adjust it for a bigger or farther screen; the percentages are fixed sizes.
        private static readonly double[][] Sizes =
        {
            new[] { 0.0, 0.85 }, new[] { 0.0, 1.0 }, new[] { 0.0, 1.25 }, new[] { 0.0, 1.5 },
            new[] { 1.0, 1.0 }, new[] { 1.25, 1.0 }, new[] { 1.5, 1.0 }, new[] { 2.0, 1.0 }, new[] { 2.5, 1.0 }, new[] { 3.0, 1.0 }
        };

        private static int SizeIndex()
        {
            double f = BrowserSettings.PageScale, b = BrowserSettings.PageBoost;
            int best = 1, at = -1;
            for (int i = 0; i < Sizes.Length; i++)
            {
                if (Math.Abs(Sizes[i][0] - f) > 0.001) continue;
                if (f <= 0 && Math.Abs(Sizes[i][1] - b) > 0.001) { if (at < 0 || Math.Abs(Sizes[i][1] - b) < Math.Abs(Sizes[at][1] - b)) at = i; continue; }
                return i;
            }
            return at >= 0 ? at : best;   // a hand-edited value in settings.json lands on the nearest Auto step
        }

        private static string SizeLabel()
        {
            double f = BrowserSettings.PageScale;
            if (f > 0) return Math.Round(f * 100).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%";
            double b = BrowserSettings.PageBoost;
            return b < 0.95 ? "Auto smaller" : b < 1.05 ? "Auto" : b < 1.35 ? "Auto larger" : "Auto largest";
        }

        private static void NextSize()
        {
            double[] next = Sizes[(SizeIndex() + 1) % Sizes.Length];
            BrowserSettings.SetPageSize(next[0], next[1]);
        }

        // choice = a row that shows its current value as a word (cycled by A) instead of an ON / OFF switch
        private void AddRow(StackPanel host, string label, int icon, Func<string> state, Action toggle, bool choice = false)
        {
            var tile = Theme.Tile(label, 24, icon, Look.Pill);
            tile.Height = 60;
            tile.Padding = new Thickness(26, 0, 200, 1);
            tile.Margin = new Thickness(0, 3, 0, 3);
            Theme.AlignLeft(tile);
            Theme.SetFocusScale(tile, 1.02);   // (the switch beside the label does not scale with it)

            // the switch: the word, then a track with a knob that sits right when on and left when off
            var word = Theme.Text("OFF", 20, Theme.Ink60);
            word.FontWeight = FontWeights.SemiBold;
            word.Margin = new Thickness(0, 0, 14, 1);
            var knob = new Ellipse { Width = 26, Height = 26, Margin = new Thickness(4), VerticalAlignment = VerticalAlignment.Center };
            var track = new Border { Width = 66, Height = 34, CornerRadius = new CornerRadius(17), Child = knob, VerticalAlignment = VerticalAlignment.Center };
            var sw = new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 28, 0), IsHitTestVisible = false
            };
            sw.Children.Add(word);
            sw.Children.Add(track);
            if (choice) { track.Visibility = Visibility.Collapsed; word.Margin = new Thickness(0, 0, 0, 1); }

            Panel.SetZIndex(sw, 5);   // above the tile (a focused tile is raised to 1)
            var g = new Grid();
            g.Children.Add(tile);
            g.Children.Add(sw);
            host.Children.Add(g);
            rows.Add(new Row { Tile = tile, Switch = sw, Word = word, Track = track, Knob = knob, State = state, Toggle = toggle, Choice = choice });
        }

        public void Open()
        {
            index = 0;
            firewallMessage = "";
            phoneNoteExtra = "";
            PhoneFirewall.CheckAsync(null);
            Visibility = Visibility.Visible;
            cardFade.Appear();
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
                bool focused = i == index;
                Theme.SetFocused(r.Tile, focused);
                string s = r.State();
                bool on = s == "ON", na = s == "N/A";
                if (r.Word.Text != s) r.Word.Text = s;
                r.Knob.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                r.Switch.Opacity = na ? 0.55 : 1;
                if (focused)   // on the focused row (filled with the accent) the switch is inverted so it stays readable
                {
                    r.Track.Background = on ? Theme.OnAccent : Theme.Tint("SeleneOnFocusColor", "#FF111111", 0.22);
                    r.Knob.Fill = on ? Theme.Accent : Theme.OnAccent;
                    r.Word.Foreground = Theme.OnAccent;
                }
                else
                {
                    r.Track.Background = on ? Theme.Accent : Theme.InkA(0.2);
                    r.Knob.Fill = on ? Theme.OnAccent : Theme.InkA(0.75);
                    r.Word.Foreground = on || r.Choice ? Theme.Ink : Theme.Ink60;
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
            string phoneText = firewallMessage;
            if (phoneText.Length == 0) phoneText = phoneNoteExtra;
            if (phoneText.Length == 0)
                phoneText = "Phone link: scan ONE code (Start menu > Phone link, or View on any keyboard) and the phone page works as the keyboard, a remote for YouTube and the browser, and a way to send links. Windows needs to allow it once (last row; it asks for admin, so use a mouse or keyboard). Passwords stay on the TV unless you allow them: the phone page is not encrypted.";
            if (phoneNote.Text != phoneText) phoneNote.Text = phoneText;
            if (adStatus.Text != status) adStatus.Text = status;
            if (adNote.Text != note) adNote.Text = note;
            adNote.Visibility = note.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
