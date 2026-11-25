// Chrome.cs - small pieces of screen chrome shared by the Browser and YouTube screens, drawn like Lanternfall's home screen:
// the clock (top right, 30 pt display face), the LB / RB shoulder badges at the ends of a tab row, and a section label.
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace SeleneBrowser
{
    /// <summary>The time of day, big and quiet, like the home screen's clock. Ticks only while it is on screen.</summary>
    internal sealed class ClockChip : TextBlock
    {
        private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };

        public ClockChip(double size)
        {
            FontFamily = Theme.Display;
            FontSize = size;
            Foreground = Theme.Ink;
            VerticalAlignment = VerticalAlignment.Center;
            IsHitTestVisible = false;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
            Tick();
            timer.Tick += delegate { Tick(); };
            Loaded += delegate { Tick(); timer.Start(); };
            Unloaded += delegate { timer.Stop(); };
            IsVisibleChanged += delegate { if (IsVisible) { Tick(); timer.Start(); } else timer.Stop(); };
        }

        private void Tick()
        {
            string t = DateTime.Now.ToString(CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern, CultureInfo.CurrentCulture);
            if (Text != t) Text = t;
        }
    }

    internal static class Chrome
    {
        /// <summary>The LB / RB badge at the ends of a tab row (the home screen's: a white rounded plate, the label in the scrim colour).</summary>
        public static FrameworkElement ShoulderBadge(string button)
        {
            string shown = Theme.PromptLabel(button);
            var plate = new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = Theme.InkA(0.92),
                Height = 26,
                MinWidth = 38,
                Padding = new Thickness(8, 0, 8, 1),
                Margin = new Thickness(8, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            plate.Child = new TextBlock
            {
                Text = shown, FontFamily = Theme.Font, FontWeight = FontWeights.Bold, FontSize = 13,
                Foreground = Theme.Get("SeleneScrimBrush", "#FF000000"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            return plate;
        }

        /// <summary>A small uppercase section label ("CONTINUE WATCHING") in the quiet ink.</summary>
        public static TextBlock SectionLabel(string text)
        {
            TextBlock t = Theme.Text(text.ToUpperInvariant(), 16, Theme.Ink60);
            t.FontWeight = FontWeights.SemiBold;
            return t;
        }
    }
}
