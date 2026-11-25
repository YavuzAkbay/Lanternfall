// YouTubeChrome.cs - the YouTube screen's own chrome, drawn like Lanternfall's home screen: the top tab bar (LB / RB badges at its
// ends, the screen's name and profile on the left, the clock on the right), the centered empty / loading / error message, the
// per-card "ambient art" link and the short fade + rise used when a screen's content changes. Colours all come from Theme's live
// brushes, so nothing here needs a rebuild when the Lanternfall theme changes. C# 5.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace SeleneBrowser
{
    /// <summary>Remembers where a card's picture lives, so the screen can melt the focused card's thumbnail into the backdrop.</summary>
    internal sealed class CardArt
    {
        private static readonly ConditionalWeakTable<Border, CardArt> table = new ConditionalWeakTable<Border, CardArt>();

        public Image Img;          // a video / playlist thumbnail
        public Ellipse Av;         // a channel avatar (an ImageBrush fill)
        public Action<ImageSource> Arrived;   // set by the screen while it waits for the picture of the focused card

        public static CardArt Attach(Border card)
        {
            CardArt a = new CardArt();
            table.Add(card, a);
            return a;
        }

        public static CardArt Of(Border card)
        {
            CardArt a;
            return card != null && table.TryGetValue(card, out a) ? a : null;
        }

        /// <summary>The picture now, or null while it is still loading (or was let go of by the grid's window).</summary>
        public ImageSource Source
        {
            get
            {
                if (Img != null) return Img.Source;
                if (Av != null)
                {
                    ImageBrush ib = Av.Fill as ImageBrush;
                    return ib != null ? ib.ImageSource : null;
                }
                return null;
            }
        }

        /// <summary>Call after the picture was set.</summary>
        public void Notify()
        {
            Action<ImageSource> cb = Arrived;
            ImageSource s = Source;
            if (cb != null && s != null) cb(s);
        }
    }

    internal static class YtFx
    {
        /// <summary>Screen content changing: a short fade and a 10 px rise (Selene's page change), 240 ms.</summary>
        public static void RiseIn(FrameworkElement el)
        {
            if (el == null) return;
            TranslateTransform tt = el.RenderTransform as TranslateTransform;
            if (tt == null || tt.IsFrozen) { tt = new TranslateTransform(0, 0); el.RenderTransform = tt; }
            IEasingFunction ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            Duration d = new Duration(TimeSpan.FromMilliseconds(240));
            // base values stay at rest (1 / 0): when the animations end the element is exactly where it belongs
            el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, d) { EasingFunction = ease });
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, d) { EasingFunction = ease });
        }

        /// <summary>A fade in (menus, toasts).</summary>
        public static void FadeIn(UIElement el, int ms)
        {
            el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(ms)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }
    }

    /// <summary>The top bar: the screen's name and the profile (left), the tab pills between LB / RB badges (centre) and the clock (right).</summary>
    internal sealed class YtTopBar : Grid
    {
        public readonly List<Border> Tabs = new List<Border>();

        public YtTopBar(string[] names, int[] icons, TextBlock profile)
        {
            Height = 110;
            Margin = new Thickness(64, 0, 64, 0);
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // left: the mark and name, the profile's name under it, small and quiet
            StackPanel left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            StackPanel brand = new StackPanel { Orientation = Orientation.Horizontal };
            FrameworkElement mark = Theme.Icon(0xE63A, 34, Theme.Accent);   // ondemand_video
            if (mark != null) { mark.Margin = new Thickness(0, 2, 12, 0); brand.Children.Add(mark); }
            brand.Children.Add(Theme.Title("YouTube", 30));
            left.Children.Add(brand);
            profile.FontSize = 18;
            profile.MaxWidth = 330;
            profile.Margin = new Thickness(0, 0, 0, 0);
            left.Children.Add(profile);
            Children.Add(left);

            // centre: LB, the tabs, RB
            StackPanel strip = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            strip.Children.Add(Chrome.ShoulderBadge("LB"));
            for (int i = 0; i < names.Length; i++)
            {
                Border t = Theme.Tile(names[i], 19, icons[i], Look.Ghost);
                t.Padding = new Thickness(18, 5, 20, 7);
                t.Margin = new Thickness(3, 0, 3, 0);
                Tabs.Add(t);
                strip.Children.Add(t);
            }
            strip.Children.Add(Chrome.ShoulderBadge("RB"));
            Grid.SetColumn(strip, 1);
            Children.Add(strip);

            // right: the clock
            ClockChip clock = new ClockChip(30);
            clock.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(clock, 2);
            Children.Add(clock);
        }

        /// <summary>The open tab is filled with the accent (as on the home screen); while the bar has the focus the focused
        /// tab is, and the open one then shows as a soft fill.</summary>
        public void Paint(int active, bool barFocused, int focusIndex)
        {
            for (int i = 0; i < Tabs.Count; i++)
            {
                if (barFocused)
                {
                    Theme.SetFocused(Tabs[i], i == focusIndex);
                    Theme.SetSelected(Tabs[i], i == active && i != focusIndex);
                }
                else
                {
                    Theme.SetFocused(Tabs[i], i == active);
                    Theme.SetSelected(Tabs[i], false);
                }
            }
        }
    }

    /// <summary>Loading / empty / error state: centred, a big quiet icon, one clear sentence and (where it helps) a focusable action pill.</summary>
    internal sealed class YtMessage : StackPanel
    {
        private readonly Spinner spinner = new Spinner(72);
        private readonly Grid iconSlot = new Grid { Width = 100, Height = 100, HorizontalAlignment = HorizontalAlignment.Center };
        private readonly TextBlock text = Theme.Title("", 34);
        private readonly TextBlock sub = Theme.Text("", 22, Theme.Ink60);
        public readonly Border Retry = Theme.Tile("Retry", 22, 0xE5D5, Look.Pill);               // refresh
        public readonly Border SearchBtn = Theme.Tile("Search YouTube", 22, 0xE8B6, Look.Pill);   // search

        /// <summary>The action pill showing now (null = none).</summary>
        public Border Action { get; private set; }

        public YtMessage()
        {
            HorizontalAlignment = HorizontalAlignment.Center;
            VerticalAlignment = VerticalAlignment.Center;
            Margin = new Thickness(64, 0, 64, 90);
            spinner.HorizontalAlignment = HorizontalAlignment.Center;
            Children.Add(spinner);
            Children.Add(iconSlot);
            text.TextWrapping = TextWrapping.Wrap;
            text.TextAlignment = TextAlignment.Center;
            text.HorizontalAlignment = HorizontalAlignment.Center;
            text.MaxWidth = 1000;
            text.Margin = new Thickness(0, 20, 0, 0);
            Children.Add(text);
            sub.TextWrapping = TextWrapping.Wrap;
            sub.TextAlignment = TextAlignment.Center;
            sub.HorizontalAlignment = HorizontalAlignment.Center;
            sub.MaxWidth = 900;
            sub.Margin = new Thickness(0, 8, 0, 0);
            Children.Add(sub);
            foreach (Border b in new Border[] { Retry, SearchBtn })
            {
                b.Padding = new Thickness(28, 10, 30, 11);
                b.Margin = new Thickness(0, 30, 0, 0);
                b.HorizontalAlignment = HorizontalAlignment.Center;
                b.Visibility = Visibility.Collapsed;
                Children.Add(b);
            }
        }

        public void ShowLoading(string t)
        {
            spinner.Visibility = Visibility.Visible;
            iconSlot.Visibility = Visibility.Collapsed;
            text.Text = t;
            sub.Visibility = Visibility.Collapsed;
            SetAction(null);
        }

        public void ShowMessage(int icon, string t, string s, Border action)
        {
            spinner.Visibility = Visibility.Collapsed;
            iconSlot.Children.Clear();
            FrameworkElement ic = icon != 0 ? Theme.Icon(icon, 88, Theme.InkA(0.45)) : null;
            if (ic != null) iconSlot.Children.Add(ic);
            iconSlot.Visibility = ic != null ? Visibility.Visible : Visibility.Collapsed;
            text.Text = t;
            sub.Text = s ?? "";
            sub.Visibility = sub.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            SetAction(action);
        }

        private void SetAction(Border action)
        {
            Action = action;
            Retry.Visibility = action == Retry ? Visibility.Visible : Visibility.Collapsed;
            SearchBtn.Visibility = action == SearchBtn ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
