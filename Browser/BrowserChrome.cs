// BrowserChrome.cs - the Browser screen's own chrome pieces, drawn like Lanternfall's home screen: a fade + slide helper (bars and
// toasts never cut in or out), the address pill (lock / search icon, a short host and the page title) and the tab pills.
// Everything uses the shared live palette brushes (Theme), so a theme change repaints it with no rebuild.
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace SeleneBrowser
{
    /// <summary>Shows and hides an element with a fade and a short slide (240 ms in, 200 ms out), then collapses it, so the
    /// layout space is freed only once it has faded. slide = how far (design units) it sits away while hidden.</summary>
    internal sealed class Fader
    {
        private readonly UIElement el;
        private readonly double slide;
        private readonly TranslateTransform move = new TranslateTransform();
        private int stamp;

        public Fader(UIElement el, double slide)
        {
            this.el = el;
            this.slide = slide;
            el.RenderTransform = move;
        }

        /// <summary>True while the element is shown or fading in.</summary>
        public bool Shown { get { return el.Visibility == Visibility.Visible; } }

        /// <summary>Starts again from hidden and fades + rises in (a panel opening).</summary>
        public void Appear()
        {
            stamp++;
            el.BeginAnimation(UIElement.OpacityProperty, null);
            move.BeginAnimation(TranslateTransform.YProperty, null);
            el.Opacity = 0;
            move.Y = slide;
            Set(true, true);
        }

        public void Set(bool show, bool animate)
        {
            int s = ++stamp;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            if (show)
            {
                if (el.Visibility != Visibility.Visible)
                {
                    el.BeginAnimation(UIElement.OpacityProperty, null);
                    move.BeginAnimation(TranslateTransform.YProperty, null);
                    el.Opacity = 0;
                    move.Y = slide;
                    el.Visibility = Visibility.Visible;
                }
                double fromO = el.Opacity, fromY = move.Y;
                el.Opacity = 1;
                move.Y = 0;
                if (!animate)
                {
                    el.BeginAnimation(UIElement.OpacityProperty, null);
                    move.BeginAnimation(TranslateTransform.YProperty, null);
                    return;
                }
                var d = TimeSpan.FromMilliseconds(240);
                el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(fromO, 1, d) { EasingFunction = ease });
                move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromY, 0, d) { EasingFunction = ease });
                return;
            }
            if (el.Visibility != Visibility.Visible) return;
            double fo = el.Opacity, fy = move.Y;
            el.Opacity = 0;
            move.Y = slide;
            if (!animate)
            {
                el.BeginAnimation(UIElement.OpacityProperty, null);
                move.BeginAnimation(TranslateTransform.YProperty, null);
                el.Visibility = Visibility.Collapsed;
                return;
            }
            var dur = TimeSpan.FromMilliseconds(200);
            var fade = new DoubleAnimation(fo, 0, dur) { EasingFunction = ease };
            fade.Completed += delegate { if (s == stamp) el.Visibility = Visibility.Collapsed; };
            el.BeginAnimation(UIElement.OpacityProperty, fade);
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fy, slide, dur) { EasingFunction = ease });
        }
    }

    /// <summary>The address pill of the top bar: a lock (https), an open lock (http) or the search glass, then the page's host
    /// in the display face and, when there is room, its title in the quiet ink. Built on a Theme pill, so focus looks like every other pill.</summary>
    internal sealed class AddressPill
    {
        public readonly Border Pill;
        private readonly TextBlock host = Theme.Text("", 22, Theme.Ink);
        private readonly TextBlock title = Theme.Text("", 22, Theme.Ink);
        private readonly FrameworkElement searchIcon, lockIcon, openIcon;
        private readonly StackPanel row;

        public AddressPill()
        {
            Pill = Theme.Tile("", 22, 0, Look.Pill);
            host.FontFamily = Theme.Display;
            host.MaxWidth = 560;
            Theme.AdoptLabel(Pill, host);
            Theme.AlignLeft(Pill);
            Theme.SetFocusScale(Pill, 1.015);
            Pill.Padding = new Thickness(26, 0, 26, 1);
            Pill.Height = 56;
            Pill.Margin = new Thickness(10, 0, 10, 0);
            Pill.HorizontalAlignment = HorizontalAlignment.Stretch;

            title.FontFamily = Theme.Display;
            title.Opacity = 0.62;
            title.Margin = new Thickness(16, 0, 0, 0);
            title.Visibility = Visibility.Collapsed;
            // follows whatever ink the pill paints its label with (rest, focused, a theme change)
            title.SetBinding(TextBlock.ForegroundProperty, new Binding("Foreground") { Source = host });

            searchIcon = MakeIcon(0xE8B6);   // search
            lockIcon = MakeIcon(0xE897);     // lock
            openIcon = MakeIcon(0xE898);     // lock_open
            row = null;
            try
            {
                var grid = Pill.Child as Grid;
                row = grid != null && grid.Children.Count > 1 ? grid.Children[1] as StackPanel : null;
            }
            catch (Exception) { row = null; }
            if (row != null)
            {
                var slot = new Grid { Width = 30, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
                AddIcon(slot, searchIcon); AddIcon(slot, lockIcon); AddIcon(slot, openIcon);
                row.Children.Insert(0, slot);
                row.Children.Add(title);
            }
            Pill.SizeChanged += delegate { FitTitle(); };
            host.SizeChanged += delegate { FitTitle(); };
            Show(searchIcon);
            Update("", "", true);
        }

        private FrameworkElement MakeIcon(int code)
        {
            FrameworkElement el = Theme.Icon(code, 26, Theme.Ink);
            var shape = el as Shape;
            if (shape != null) shape.SetBinding(Shape.FillProperty, new Binding("Foreground") { Source = host });
            return el;
        }

        private static void AddIcon(Grid slot, FrameworkElement icon)
        {
            if (icon == null) return;
            icon.Visibility = Visibility.Collapsed;
            slot.Children.Add(icon);
        }

        private void Show(FrameworkElement icon)
        {
            FrameworkElement[] all = { searchIcon, lockIcon, openIcon };
            foreach (FrameworkElement e in all)
                if (e != null) e.Visibility = ReferenceEquals(e, icon) ? Visibility.Visible : Visibility.Collapsed;
        }

        // the title takes what the host leaves of the pill (trimmed with an ellipsis), and goes when under ~120 units remain
        private void FitTitle()
        {
            if (title.Text.Length == 0) { title.Visibility = Visibility.Collapsed; return; }
            double room = Pill.ActualWidth - Pill.Padding.Left - Pill.Padding.Right - 46 - host.ActualWidth - 16;
            if (room < 120) { title.Visibility = Visibility.Collapsed; return; }
            title.MaxWidth = room;
            title.Visibility = Visibility.Visible;
        }

        /// <summary>address: the page's URL (ignored on the start page); pageTitle: its document title.</summary>
        public void Update(string address, string pageTitle, bool home)
        {
            string t = (pageTitle ?? "").Trim();
            if (home || string.IsNullOrEmpty(address))
            {
                host.Text = "Search or type a URL";
                host.Opacity = 0.7;
                title.Text = "";
                Show(searchIcon);
                FitTitle();
                return;
            }
            host.Opacity = 1;
            string shownHost = address;
            FrameworkElement icon = searchIcon;
            Uri u;
            if (Uri.TryCreate(address, UriKind.Absolute, out u))
            {
                if (u.Scheme == Uri.UriSchemeHttps) icon = lockIcon;
                else if (u.Scheme == Uri.UriSchemeHttp) icon = openIcon;
                if (!string.IsNullOrEmpty(u.Host))
                {
                    shownHost = u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? u.Host.Substring(4) : u.Host;
                }
            }
            host.Text = shownHost;
            if (t.Length > 80) t = t.Substring(0, 79) + "…";
            // a title that only repeats the address says nothing
            title.Text = (t.Length == 0 || string.Equals(t, address, StringComparison.OrdinalIgnoreCase) || string.Equals(t, shownHost, StringComparison.OrdinalIgnoreCase)) ? "" : t;
            Show(icon ?? searchIcon);
            FitTitle();
        }
    }

    internal static class ChromeParts
    {
        /// <summary>One tab of the strip: a pill with a letter mark (the home icon on the start page) and the page title. The open
        /// tab is filled softly with the accent on its mark; the others sit as glass.</summary>
        public static Border TabPill(string label, bool isActive, bool home)
        {
            Border b = Theme.Tile(label, 20, 0, Look.Pill);
            Theme.AlignLeft(b);
            Theme.SetSelected(b, isActive);
            Theme.SetFocusScale(b, 1);
            b.Height = 46;
            b.MaxWidth = 300;
            b.Padding = new Thickness(9, 0, 24, 1);
            b.Margin = new Thickness(5, 0, 5, 0);
            b.IsHitTestVisible = false;
            TextBlock text = Theme.LabelOf(b);
            if (text != null) { text.MaxWidth = 200; text.Opacity = isActive ? 1 : 0.8; }

            var mark = new Grid { Width = 30, Height = 30, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            mark.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(15),
                Background = isActive ? Theme.Accent : Theme.InkA(0.16)
            });
            FrameworkElement glyph = home ? Theme.Icon(0xE88A, 18, isActive ? Theme.OnAccent : Theme.Ink) : null;   // home
            if (glyph == null)
            {
                string initial = string.IsNullOrEmpty(label) ? "?" : label.Substring(0, 1).ToUpperInvariant();
                glyph = new TextBlock
                {
                    Text = initial, FontFamily = Theme.Display, FontWeight = FontWeights.SemiBold, FontSize = 16,
                    Foreground = isActive ? Theme.OnAccent : Theme.Ink,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 1)
                };
            }
            mark.Children.Add(glyph);
            try
            {
                var grid = b.Child as Grid;
                var row = grid != null && grid.Children.Count > 1 ? grid.Children[1] as StackPanel : null;
                if (row != null) row.Children.Insert(0, mark);
            }
            catch (Exception) { }
            return b;
        }
    }
}
