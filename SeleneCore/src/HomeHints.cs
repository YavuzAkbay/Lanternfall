using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace Selene
{
    // The home's button hints along the bottom: on the left what's always there (View: library, Menu, with their holds),
    // on the right what A / X / Y / R3 do for whatever is focused right now
    public static class HomeHints
    {
        private static Grid host;
        private static StackPanel bar, always;
        private static UIElement filterHint;   // Lanternfall: only while the top filter tabs are hidden
        private static string shown = "";

        public static void Attach(Window w)
        {
            AttachLibrary(w);
            var hub = HomeNav.FindByName(w, "WelcomeHubOverlay") as Grid;
            if (hub == null || host == hub) return;
            host = hub;
            bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 64, 40) };   // (clickable: a hint presses its button, #40)
            Panel.SetZIndex(bar, 60);
            Grid.SetRowSpan(bar, 99);
            hub.Children.Add(bar);
            always = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(64, 0, 0, 40) };
            always.Children.Add(Hold(Ui.Prompt("View", "Collection"), "Hold: Search"));
            always.Children.Add(Hold(Ui.Prompt("Start", "Menu"), "Hold: Settings"));
            filterHint = Ui.Prompt("LB", "Filter");
            always.Children.Add(filterHint);
            Panel.SetZIndex(always, 60);
            Grid.SetRowSpan(always, 99);
            hub.Children.Add(always);
            hub.AddHandler(UIElement.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((s, e) => Update()), true);
            HintPeek.Attach(w, bar, always);
            Update();
        }

        // Collection's hints are the theme's: each names its button in its Tag, and a click inside one presses that button (#40).
        // Found by where the click lands, not by what WPF hit first: over the right half of the bar Collection's background art
        // took the click instead.
        private static Window libraryWindow;
        private static void AttachLibrary(Window w)
        {
            if (w == null || ReferenceEquals(w, libraryWindow)) return;
            libraryWindow = w;
            // right-click, or the mouse's Back button, is B: back out of whatever is open (a mouse has no B, #40)
            w.AddHandler(UIElement.PreviewMouseUpEvent, new MouseButtonEventHandler((s, e) =>
            {
                if (e.ChangedButton != MouseButton.Right && e.ChangedButton != MouseButton.XButton1) return;
                e.Handled = true;
                if (LoginScene.IsActive) LoginScene.MousePress("B"); else Ui.PressHint("B");
            }), true);
            w.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((s, e) =>
            {
                var lib = HomeNav.FindByName(w, "SeleneLibraryHints") as FrameworkElement;
                if (lib == null || !lib.IsVisible || LoginScene.IsActive) return;
                foreach (var hint in HomeNav.FindTagged(lib))
                {
                    if (!hint.IsVisible || hint.ActualWidth < 1) continue;
                    var at = e.GetPosition(hint);
                    if (at.X < 0 || at.Y < -6 || at.X > hint.ActualWidth || at.Y > hint.ActualHeight + 6) continue;
                    e.Handled = true;
                    Ui.PressHint((string)hint.Tag);
                    return;
                }
            }), true);
        }

        // what's focused decides the hints; the bar crossfades when they change
        public static void Update()
        {
            if (bar == null) return;
            bar.Visibility = Features.On("hints") ? Visibility.Visible : Visibility.Collapsed;
            if (always != null) always.Visibility = bar.Visibility;
            if (filterHint != null) filterHint.Visibility = FilterFeedback.Hidden ? Visibility.Visible : Visibility.Collapsed;
            var focused = Keyboard.FocusedElement as FrameworkElement;
            string key;
            string[] hints;
            var item = focused as ListBoxItem;
            if (HomeNav.Moving) { key = "move"; hints = new[] { "A", "Drop", "B", "Cancel" }; }
            else if (item != null && HomeNav.InRow(item))
            {
                if (item.DataContext is string) { key = "card"; hints = new[] { "Y", "Move", "A", "Open" }; }
                else if (HomeNav.HasTrailerSound(item.DataContext)) { key = "game+"; hints = new[] { "R3", "Trailer sound", "X", "Details", "Y", "Game options", "A", "Play" }; }
                else { key = "game"; hints = new[] { "X", "Details", "Y", "Game options", "A", "Play" }; }
            }
            else if (focused != null && focused.Name.StartsWith("SeleneShotTile")) { key = "shot"; hints = new[] { "A", "View" }; }
            else if (focused != null && focused.Name.StartsWith("SeleneGameNewsTile")) { key = "gamenews"; hints = new[] { "A", "Read" }; }
            else if (focused != null && focused.Name.StartsWith("SeleneYouTubeFeedTile")) { key = "ytfeed"; hints = new[] { "A", "Play" }; }
            else if (focused != null && focused.Name.StartsWith("SeleneBrowserFeedTile")) { key = "browserfeed"; hints = new[] { "A", "Open" }; }
            else if (focused != null && focused.Name == "SeleneTopAchievements") { key = "topachievements"; hints = new[] { "A", "Achievements", "B", "Back" }; }
            else if (focused != null && (focused.Name == "SelenePlayBtn" || focused.Name == "SeleneDetailsBtn" || focused.Name == "SeleneFavBtn" || focused.Name == "SeleneAchBtn")) { key = "pill"; hints = new[] { "A", "Select" }; }
            else { key = "other"; hints = new string[0]; }
            if (Ui.PlayStationPrompts) key += "|ps";   // the button names changed (Xbox / PlayStation): built again
            if (key == shown) return;
            shown = key;
            bar.Children.Clear();
            for (int i = 0; i + 1 < hints.Length; i += 2) bar.Children.Add(Ui.Prompt(hints[i], hints[i + 1]));
            if (!HintPeek.OwnsOpacity) bar.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = Ui.Out() });
        }

        // a hint with what holding the button does, small under its label
        private static StackPanel Hold(StackPanel prompt, string hold)
        {
            var label = prompt.Children.Count > 1 ? prompt.Children[1] as TextBlock : null;
            if (label == null) return prompt;
            prompt.Children.Remove(label);
            var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = label.Margin };
            label.Margin = new Thickness(0);
            lines.Children.Add(label);
            lines.Children.Add(new TextBlock { Text = hold, FontSize = 13, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.6) });
            prompt.Children.Add(lines);
            return prompt;
        }
    }
}
