using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Selene
{
    // Type on a phone, for the pad keyboards of Core's own screens (Find a game, IGDB search, Add feed). View shows a QR card on the
    // right of the screen; the phone opens a page from this PC (over the home network) and its text box follows the screen's field
    // both ways. The server, the settings and the firewall check live in the Lanternfall Browser add-on (SeleneBrowser.PhoneBridge,
    // found by reflection like the other Browser hooks); without it, or with an older one, View says so. New file: fork-only.
    //
    // A screen creates one PhoneType and calls Toggle() on View, Changed() after the pad edited the text, Close() when it leaves.
    //   get()   the field's text now        set(t)  the phone typed: the field now holds t        done()  the phone's Done (Start)
    public sealed class PhoneType
    {
        private readonly Screen screen;
        private readonly string title;
        private readonly Func<string> get;
        private readonly Action<string> set;
        private readonly Action done;
        private readonly bool multiline;
        private object handle;
        private Border card;
        private TextBlock status;
        private DispatcherTimer watch;
        private int ticks;
        private bool connected;

        private static MethodInfo start;
        private static bool looked;

        public PhoneType(Screen screen, string title, Func<string> get, Action<string> set, Action done, bool multiline = false)
        {
            this.screen = screen; this.title = title; this.get = get; this.set = set; this.done = done; this.multiline = multiline;
        }

        public bool IsOpen { get { return card != null; } }

        public void Toggle()
        {
            if (IsOpen) Close(); else Open();
        }

        // the pad changed the text: the phone's box follows
        public void Changed()
        {
            if (handle == null) return;
            try { handle.GetType().GetMethod("SetText").Invoke(handle, new object[] { get() ?? "" }); } catch { }
        }

        public void Close()
        {
            if (watch != null) { watch.Stop(); watch = null; }
            var h = handle;
            handle = null;
            if (h != null) { try { h.GetType().GetMethod("Close").Invoke(h, null); } catch { } }
            if (card != null) { screen.Root.Children.Remove(card); card = null; }
        }

        private static MethodInfo Start()
        {
            if (looked) return start;
            looked = true;
            try
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "SeleneBrowser");
                var type = asm == null ? null : asm.GetType("SeleneBrowser.PhoneBridge");
                start = type == null ? null : type.GetMethod("Start");
            }
            catch (Exception ex) { HomeNav.Log("phone type: " + ex.Message); }
            return start;
        }

        private static string AccentHex()
        {
            try { var c = Ink.Of("SeleneFocusColor"); return "#" + c.R.ToString("x2") + c.G.ToString("x2") + c.B.ToString("x2"); }
            catch { return null; }
        }

        private static void OnUi(Action a)
        {
            var app = Application.Current;
            if (app == null) return;
            try { app.Dispatcher.BeginInvoke(a); } catch { }
        }

        private void Open()
        {
            var method = Start();
            if (method == null)
            {
                Show(null, "The phone keyboard needs the newer Lanternfall Browser add-on. Update it in the menu, then try again.");
                return;
            }
            string url = null, message = null;
            try
            {
                Action<string> text = t => OnUi(() => { if (handle == null) return; set(t ?? ""); });
                Action phoneDone = () => OnUi(() => { if (handle != null) done(); });
                Action cancelled = () => OnUi(() => { if (handle != null) Close(); });
                Action phoneConnected = () => OnUi(() =>
                {
                    connected = true;
                    if (status != null) status.Text = Lang.T("Phone connected. Type there or here: both work.");
                });
                handle = method.Invoke(null, new object[] { title, get() ?? "", multiline, false, AccentHex(), text, phoneDone, cancelled, phoneConnected });
                var type = handle.GetType();
                url = type.GetProperty("Url").GetValue(handle, null) as string;
                message = type.GetProperty("Message").GetValue(handle, null) as string;
            }
            catch (Exception ex)
            {
                HomeNav.Log("phone type: start failed: " + (ex.InnerException ?? ex).Message);
                handle = null;
                message = "The phone keyboard could not start.";
            }
            if (url == null) { handle = null; Show(null, message ?? "The phone keyboard is not available."); return; }
            connected = false;
            Show(url, Lang.T("Waiting for your phone..."));
            ticks = 0;
            watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            watch.Tick += (s, e) =>
            {
                ticks++;
                if (handle == null || connected || status == null) { watch.Stop(); return; }
                try
                {
                    var hint = handle.GetType().GetProperty("Hint").GetValue(handle, null) as string;
                    if (hint != null) { status.Text = Lang.T(hint); return; }
                }
                catch { }
                if (ticks == 14) status.Text = Lang.T("Still nothing? Check that your phone is on the same Wi-Fi as this PC, not on mobile data.");
            };
            watch.Start();
        }

        // the card: the code (or just the message) on the right of the screen
        private void Show(string url, string line)
        {
            if (card != null) { screen.Root.Children.Remove(card); card = null; }
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = Lang.T("Type on your phone"), FontSize = 26, FontFamily = Ui.Display, FontWeight = FontWeights.SemiBold,
                Foreground = Ink.Brush("SeleneInkColor"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14)
            });
            if (url != null)
            {
                try
                {
                    stack.Children.Add(new Border
                    {
                        Background = Brushes.White, CornerRadius = new CornerRadius(14), Padding = new Thickness(3), HorizontalAlignment = HorizontalAlignment.Left,
                        Child = new Image { Source = new QrCode(url).Picture(), Width = 250, Height = 250, Stretch = Stretch.Uniform }
                    });
                    stack.Children.Add(new TextBlock
                    {
                        Text = Lang.T("Scan it with your phone's camera. The phone and this PC need to be on the same Wi-Fi."), FontSize = 17, FontFamily = Ui.Body,
                        Foreground = Ink.Brush("SeleneInkColor", 0.75), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0)
                    });
                }
                catch (Exception ex) { HomeNav.Log("phone type: code: " + ex.Message); line = "The code could not be drawn."; }
            }
            status = new TextBlock
            {
                Text = line, FontSize = 18, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneFocusColor"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0)
            };
            stack.Children.Add(status);
            stack.Children.Add(new TextBlock
            {
                Text = Lang.T("View closes this"), FontSize = 16, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.55), Margin = new Thickness(0, 12, 0, 0)
            });
            card = new Border
            {
                Width = 330, Padding = new Thickness(24, 22, 24, 22), CornerRadius = new CornerRadius(26),
                Background = Ink.Brush("SeleneGlassColor", 0.95), BorderBrush = Ink.Brush("SeleneInkColor", 0.16), BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 36, 0),
                Child = stack, Opacity = 0
            };
            Panel.SetZIndex(card, 50);
            screen.Root.Children.Add(card);
            Ui.Fade(card, 1, 220);
        }
    }
}
