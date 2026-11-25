// PhoneLinkCard.cs - the "Phone link" card (Start menu > Phone link, in the browser and in YouTube): the ONE QR code the phone scans once.
// The phone page it opens follows the TV (keyboard, YouTube, browser, send a link). Opening the card switches the phone link on
// (remembered); X makes a new address, Y turns it off.
// Built like the settings card (Theme helpers, scrim behind). C# 5 only.
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace SeleneBrowser
{
    internal sealed class PhoneLinkCard : Grid
    {
        private readonly Image qr = new Image { Width = 300, Height = 300, Stretch = Stretch.Uniform };
        private readonly Border qrFrame = new Border();
        private readonly TextBlock head = Theme.Title("Phone link", 34);
        private readonly TextBlock body = Theme.Text("", 23, Theme.Ink);
        private readonly TextBlock link = Theme.Text("", 19, Theme.Ink60);
        private readonly TextBlock status = Theme.Text("", 21, Theme.Accent);
        private readonly TextBlock keys = Theme.Text("B  Close     X  New address (disconnects phones)     Y  Turn off", 19, Theme.Ink60);
        private readonly Border card = new Border();
        private readonly Fader fade;
        private readonly DispatcherTimer tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        private string shownUrl;
        private int openedAt;

        /// <summary>Raised after the card closed, whatever the reason (the screen behind updates its hints).</summary>
        public event Action Closed;

        public bool IsOpen { get { return Visibility == Visibility.Visible; } }

        public PhoneLinkCard()
        {
            Background = Theme.ScrimA(0.62);
            Visibility = Visibility.Collapsed;
            qrFrame.Background = Brushes.White;
            qrFrame.CornerRadius = new CornerRadius(16);
            qrFrame.Padding = new Thickness(4);
            qrFrame.Child = qr;
            qrFrame.VerticalAlignment = VerticalAlignment.Top;
            qrFrame.Margin = new Thickness(0, 0, 32, 0);
            head.Margin = new Thickness(0, 0, 0, 10);
            body.TextWrapping = TextWrapping.Wrap;
            link.TextWrapping = TextWrapping.Wrap;
            link.Margin = new Thickness(0, 12, 0, 0);
            status.TextWrapping = TextWrapping.Wrap;
            status.Margin = new Thickness(0, 14, 0, 0);
            keys.TextWrapping = TextWrapping.Wrap;
            keys.Margin = new Thickness(0, 16, 0, 0);
            var info = new StackPanel { Width = 520, VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(head);
            info.Children.Add(body);
            info.Children.Add(link);
            info.Children.Add(status);
            info.Children.Add(keys);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(qrFrame);
            row.Children.Add(info);
            card.Padding = new Thickness(36, 32, 40, 32);
            card.Child = row;
            card.HorizontalAlignment = HorizontalAlignment.Center;
            card.VerticalAlignment = VerticalAlignment.Center;
            Theme.Glass(card, 30);
            fade = new Fader(card, 12);
            Children.Add(new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(40, 24, 40, 24), Child = card });
            tick.Tick += delegate { Refresh(); };
            RemoteService.Sent += delegate { try { Dispatcher.BeginInvoke(new Action(delegate { if (IsOpen) Close(); })); } catch { } };
        }

        public void Open()
        {
            shownUrl = null;
            RemoteService.Enable();
            Visibility = Visibility.Visible;
            openedAt = Environment.TickCount;
            fade.Appear();
            Refresh();
            tick.Start();
        }

        public void Close()
        {
            tick.Stop();
            bool was = IsOpen;
            Visibility = Visibility.Collapsed;
            if (was) { var h = Closed; if (h != null) h(); }
        }

        /// <summary>Pad input while open. True when the card should close.</summary>
        public bool HandlePad(string b, RepeatLimiter limiter)
        {
            switch (b)
            {
                case "B": case "Start": case "A": return limiter.IsFirst(b);
                case "X":
                    if (limiter.IsFirst(b)) { RemoteService.Reset(); shownUrl = null; openedAt = Environment.TickCount; Refresh(); }
                    break;
                case "Y":
                    if (limiter.IsFirst(b)) { RemoteService.Disable(); Refresh(); }
                    break;
            }
            return false;
        }

        private void Note(string title, string text)
        {
            head.Text = title;
            body.Text = text;
            link.Text = "";
            status.Text = "";
            qrFrame.Visibility = Visibility.Collapsed;
        }

        private void Refresh()
        {
            if (!BrowserSettings.PhoneRemote)
            {
                Note("Phone link is off", "Close this and choose Phone link again to turn it on.");
                return;
            }
            PhoneRemote r = RemoteService.Remote;
            string url = r.Url;
            if (!r.Running || url == null)
            {
                Note("Phone link unavailable", RemoteService.Error ?? "It could not start.");
                return;
            }
            head.Text = "Phone link";
            body.Text = "Scan the code once with your phone's camera (same Wi-Fi as this PC). One page does it all: it shows the keyboard when you type here, remote controls for YouTube and the browser, and a box to send links.";
            qrFrame.Visibility = Visibility.Visible;
            if (url != shownUrl)
            {
                try { qr.Source = new QrCode(url).Picture(); shownUrl = url; }
                catch (Exception ex) { Note("Phone link unavailable", "The code could not be drawn: " + ex.Message); return; }
            }
            link.Text = url.Replace("http://", "");
            if (r.PhoneHere) status.Text = "Phone connected. Use it as a keyboard, a remote, or to send links.";
            else
            {
                FirewallState f = PhoneFirewall.Last;
                bool slow = unchecked(Environment.TickCount - openedAt) > 12000;
                if (slow && (f == FirewallState.Missing || f == FirewallState.Blocked))
                    status.Text = "Windows Firewall may stop your phone from connecting. Set it up once in the Browser's Settings (needs a mouse or keyboard).";
                else if (slow) status.Text = "Still nothing? Check that your phone is on the same Wi-Fi as this PC, not on mobile data.";
                else status.Text = "Waiting for your phone...";
            }
        }
    }
}
