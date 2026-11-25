using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Selene
{
    // Lanternfall: with the permanent filter tabs hidden, a PgUp / PgDn (LB / RB) switch shows this glass pill just
    // above the game row for a moment: the filter you landed on, how many games it holds, and the keys to keep moving.
    // It never stays on screen, so the top of the home stays clean in the minimal setup.
    public static class FilterFeedback
    {
        private static Border pill;
        private static TextBlock nameText;
        private static TextBlock countText;
        private static DispatcherTimer timer;

        // the tabs only go away together with the peekable hints that replace them: hints off keeps the tabs
        public static bool Hidden { get { return Features.On("guidance") && Features.On("hints"); } }

        // called from HomeNav.ApplyRowFilter; dir == 0 is a rebuild, not a switch, so only real switches get a pill
        public static void Show(ListBox row, string name, int count, int dir)
        {
            if (!Hidden || dir == 0 || string.IsNullOrEmpty(name)) return;
            var window = Window.GetWindow(row);
            var host = window != null ? HomeNav.FindByName(window, "SeleneRowIntro") as Panel : null;
            if (host == null) return;
            if (pill == null || VisualTreeHelper.GetParent(pill) != host)
            {
                if (pill != null)
                {
                    var old = VisualTreeHelper.GetParent(pill) as Panel;
                    if (old != null) old.Children.Remove(pill);
                }
                Build(host);
            }
            nameText.Text = name;
            countText.Text = count.ToString();
            if (timer != null) { timer.Stop(); timer = null; }
            pill.BeginAnimation(UIElement.OpacityProperty, null);
            pill.Visibility = Visibility.Visible;
            var rise = new TranslateTransform(0, 12);
            pill.RenderTransform = rise;
            Ui.Fade(pill, 1, 170);
            rise.BeginAnimation(TranslateTransform.YProperty, Ui.Fast(new DoubleAnimation(0, TimeSpan.FromMilliseconds(240)) { EasingFunction = Ui.Out() }));
            timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1900) };
            timer.Tick += delegate { Hide(); };
            timer.Start();
        }

        public static void Hide()
        {
            if (timer != null) { timer.Stop(); timer = null; }
            if (pill == null) return;
            pill.BeginAnimation(UIElement.OpacityProperty, null);
            Ui.Fade(pill, 0, 360, 0, delegate { if (timer == null && pill != null) pill.Visibility = Visibility.Collapsed; });
        }

        private static void Build(Panel host)
        {
            nameText = new TextBlock
            {
                FontSize = 19, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"), FontWeight = FontWeights.SemiBold,
                Foreground = Ink.Brush("SeleneInkColor"), VerticalAlignment = VerticalAlignment.Center
            };
            countText = new TextBlock
            {
                FontSize = 16, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
                Foreground = Ink.Brush("SeleneInkColor", 0.7), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 1, 0, 0)
            };
            var upText = new TextBlock { FontSize = 12, FontWeight = FontWeights.Bold, Foreground = Ink.Brush("SeleneScrimColor"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var downText = new TextBlock { FontSize = 12, FontWeight = FontWeights.Bold, Foreground = Ink.Brush("SeleneScrimColor"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var up = new Border { Child = upText, Height = 24, MinWidth = 38, Padding = new Thickness(6, 0, 6, 0), CornerRadius = new CornerRadius(8), Background = Ink.Brush("SeleneInkColor"), VerticalAlignment = VerticalAlignment.Center };
            var down = new Border { Child = downText, Height = 24, MinWidth = 38, Padding = new Thickness(6, 0, 6, 0), CornerRadius = new CornerRadius(8), Background = Ink.Brush("SeleneInkColor"), VerticalAlignment = VerticalAlignment.Center };
            // the badges read PgUp / PgDn on a keyboard and LB / RB (or L1 / R1) on a pad, like the old top bar did
            Ui.LiveHint(up, delegate { upText.Text = "\u25C0 " + Ui.PromptLabel("LB"); });
            Ui.LiveHint(down, delegate { downText.Text = Ui.PromptLabel("RB") + " \u25B6"; });

            var middle = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
            middle.Children.Add(nameText);
            middle.Children.Add(countText);
            var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(up);
            content.Children.Add(middle);
            content.Children.Add(down);
            pill = new Border
            {
                Child = content, CornerRadius = new CornerRadius(20), Padding = new Thickness(10, 8, 10, 9),
                Background = Ink.Brush("SeleneGlassColor", 0.62), BorderBrush = Ink.Brush("SeleneInkColor", 0.28), BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -34, 0, 0), IsHitTestVisible = false, Opacity = 0, Visibility = Visibility.Collapsed
            };
            Panel.SetZIndex(pill, 60);
            host.Children.Add(pill);
        }
    }
}
