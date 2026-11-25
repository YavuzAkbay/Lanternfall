// RestChip.cs - a passive, non-interactive "controller resting" reminder read from the AppDomain slot SeleneCore
// publishes ("Lanternfall.PadRestPrompt", the same shared-process pattern as Presence). The chip is drawn only while
// its surface is shown; it never takes focus, hit-testing, pad/key input, touches hints, media or overlay lifecycle.
// C# 5 only.
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SeleneBrowser
{
    internal sealed class RestChip
    {
        public const string Slot = "Lanternfall.PadRestPrompt";

        private readonly Border chip;
        private readonly TextBlock label;
        private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };

        private string lastValue = null;   // last slot value applied (null = nothing applied yet)
        private int token;                 // stamps updates so a late fade/hide cannot clobber a newer state
        private long autoHideAt;           // Environment.TickCount deadline for passive reminders, 0 = stay until cleared

        public RestChip()
        {
            label = Theme.Text("", 20, Theme.Ink);   // the theme's font, couch-readable
            label.IsHitTestVisible = false;
            label.Focusable = false;
            label.TextWrapping = TextWrapping.Wrap;
            label.MaxWidth = 1100;
            chip = new Border
            {
                Child = label,
                Padding = new Thickness(26, 10, 28, 11),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
                Focusable = false,
                Visibility = Visibility.Collapsed,
                Opacity = 0
            };
            Theme.Glass(chip, 24);   // the same glass as the toast and the menus
            chip.SizeChanged += delegate { chip.CornerRadius = new CornerRadius(Math.Max(0, chip.ActualHeight / 2)); };   // a pill: radius = height / 2
            timer.Tick += OnTick;
        }

        /// <summary>The chip's visual. The owner positions it and adds it to the right (non-windowed / overlay) grid.</summary>
        public Border View { get { return chip; } }

        public void Start()
        {
            Read();                       // show immediately if a value is already published
            if (!timer.IsEnabled) timer.Start();
        }

        public void Stop()
        {
            timer.Stop();
            autoHideAt = 0;
            lastValue = "";               // a later Start() re-reads even if the slot still holds the same value
            token++;
            Fade(0, token);
        }

        private void OnTick(object sender, EventArgs e)
        {
            Read();
            if (autoHideAt != 0 && Environment.TickCount >= autoHideAt)
            {
                autoHideAt = 0;
                token++;
                Fade(0, token);           // passive reminder expired; the slot may still hold the value
            }
        }

        // AppDomain slot -> string; no I/O, safe on the UI thread.
        private void Read()
        {
            try
            {
                object o = AppDomain.CurrentDomain.GetData(Slot);
                string v = o as string ?? "";
                if (v == lastValue) return;
                lastValue = v;
                Apply(v);
            }
            catch { }
        }

        private void Apply(string value)
        {
            token++;
            autoHideAt = 0;
            string text = Text(value);
            if (text == null) { Fade(0, token); return; }   // "" / null / unknown -> hide

            label.Text = text;
            double target;
            if (value == "armed" || value == "off") target = 0.5;   // persistent but very subtle
            else if (value == "verify") target = 0.82;              // an active instruction, stays until it changes
            else target = 0.92;                                     // low / verified / verifyfail: passive reminder
            if (value == "low" || value == "verified" || value == "verifyfail")
                autoHideAt = Environment.TickCount + 8000;
            chip.Visibility = Visibility.Visible;
            Fade(target, token);
        }

        private static string Text(string value)
        {
            switch (value)
            {
                case "low": return "Controller idle - extra pad polling is paused";
                case "armed": return "Controller resting - hold Xbox 6 s to switch it off (short presses are ignored)";
                case "off": return "Controller off - press Xbox to wake it";
                case "verify": return "Verification - hold the home button (Xbox) for 8 s, keep Playnite in front";
                case "verified": return "Verified - Power off is ready to switch on";
                case "verifyfail": return "Verification failed - Windows took focus. Turn off Game Bar controller binding and Xbox mode.";
                default: return null;
            }
        }

        private void Fade(double to, int stamp)
        {
            double from = chip.Opacity;   // current (possibly mid-animation) value
            chip.Opacity = to;            // base value stays at the destination once the animation holds
            var a = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(to > 0 ? 240 : 420));
            a.Completed += delegate(object sender, EventArgs e)
            {
                if (stamp != token) return;
                if (to <= 0) chip.Visibility = Visibility.Collapsed;
            };
            chip.BeginAnimation(UIElement.OpacityProperty, a);
        }
    }
}
