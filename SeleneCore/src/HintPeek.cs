using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Selene
{
    // Lanternfall: the bottom hints are a peek, not furniture. Hold L3 (or H on a keyboard) and they fade in;
    // let go and they stay for a moment, then fade out. With the setting off, HomeHints keeps its usual look.
    public static class HintPeek
    {
        private const int HoldMs = 250, LingerMs = 3000, FadeInMs = 150, FadeOutMs = 350;

        private static StackPanel bar, always;
        private static FrameworkElement library;
        private static DispatcherTimer holdTimer, lingerTimer;
        private static bool padDown, keyDown, shown, attached;

        // HomeHints leaves the opacity alone while this is on: the peek owns it.
        public static bool OwnsOpacity { get { return On; } }
        public static bool On { get { return Features.On("guidance") && Features.On("hints"); } }

        public static void Attach(Window w, StackPanel right, StackPanel left)
        {
            bar = right;
            always = left;
            library = w != null ? HomeNav.FindByName(w, "SeleneLibraryHints") as FrameworkElement : null;
            if (!attached)
            {
                attached = true;
                Ui.ButtonReleased += OnReleased;
                // the release can happen while another window has the focus: forget the held state, or the next press is ignored
                if (w != null) w.Deactivated += delegate { padDown = false; keyDown = false; EndHold(); };
            }
            ApplySettings();
        }

        public static void ApplySettings()
        {
            if (!attached) return;
            shown = false;
            StopHold();
            StopLinger();
            if (On) { SetHit(false); Put(0); }
            else { SetHit(true); Put(1); }
        }

        // ---- the trigger ----
        public static void PadDown()
        {
            if (!On || padDown) return;
            padDown = true;
            BeginHold();
        }

        public static void KeyDown()
        {
            if (!On || keyDown || Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is RichTextBox) return;
            keyDown = true;
            BeginHold();
        }

        private static void OnReleased(string button) { if (button == "LeftStick") PadUp(); }
        public static void PadUp() { if (!padDown) return; padDown = false; EndHold(); }
        public static void KeyUp() { if (!keyDown) return; keyDown = false; EndHold(); }

        private static void BeginHold()
        {
            StopLinger();
            if (shown) return;
            StopHold();
            holdTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(HoldMs) };
            holdTimer.Tick += delegate { StopHold(); Reveal(); };
            holdTimer.Start();
        }

        private static void EndHold()
        {
            StopHold();
            if (!shown) return;
            StopLinger();
            lingerTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(LingerMs) };
            lingerTimer.Tick += delegate { StopLinger(); Conceal(); };
            lingerTimer.Start();
        }

        private static void StopHold() { if (holdTimer != null) { holdTimer.Stop(); holdTimer = null; } }
        private static void StopLinger() { if (lingerTimer != null) { lingerTimer.Stop(); lingerTimer = null; } }

        // ---- showing and hiding ----
        private static void Reveal()
        {
            if (!On || shown || HomeNav.HintsBlocked()) return;
            shown = true;
            SetHit(true);
            FadeTo(1, FadeInMs);
        }

        private static void Conceal()
        {
            if (!shown) return;
            shown = false;
            SetHit(false);
            FadeTo(0, FadeOutMs);
        }

        private static void FadeTo(double to, int ms)
        {
            Fade(bar, to, ms);
            Fade(always, to, ms);
            Fade(library, to, ms);
        }

        private static void Fade(UIElement el, double to, int ms)
        {
            if (el == null) return;
            el.BeginAnimation(UIElement.OpacityProperty, Ui.Fast(new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Ui.Out() }));
        }

        // invisible hints must not swallow clicks meant for the game row underneath
        private static void SetHit(bool on)
        {
            if (bar != null) bar.IsHitTestVisible = on;
            if (always != null) always.IsHitTestVisible = on;
            if (library != null) library.IsHitTestVisible = on;
        }

        private static void Put(double opacity)
        {
            Put(bar, opacity);
            Put(always, opacity);
            Put(library, opacity);
        }

        private static void Put(UIElement el, double opacity)
        {
            if (el == null) return;
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Opacity = opacity;
        }
    }
}
