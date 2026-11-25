// OnScreenKeyboard.cs - pad-driven on-screen keyboard overlay (typing URLs, search boxes, form fields).
// Put it on top of the page in the same Grid cell; it stays collapsed until Open(). While IsOpen the host
// must route every pad button to HandlePad (it is modal and always consumes).
// Drawn as a glass sheet docked at the bottom of the screen over the dimmed page: a typed-text field with a blinking caret on top,
// a strip of suggestions, then the keys (64 high, 26 pt letters). Phone-like pages: letters, then More (symbols, Clear) and accents.
//   D-pad move (clamped in the grid, wraps in the suggestion strip)  Up picks a word  A type/pick
//   X backspace  Y space  LB/RB move text cursor  LT shift  RT letters/More  View type on the phone (QR code)  Start done  B cancel
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace SeleneBrowser
{
    public class OnScreenKeyboard : UserControl
    {
        private enum Kind { Char, Shift, Space, Back, Clear, Done, Page }

        private sealed class Key
        {
            public Kind Kind;
            public string Lower, Upper;
            public double Width = 104;
            public int Target;   // Kind.Page: the page this key opens
            public Border Box;
            public TextBlock Label;
        }

        public const string Hints = "A Type/Pick    X Delete    Y Space    LB/RB Cursor    LT Shift    RT More    Back Phone    Start Done    B Cancel";

        // Like a phone: letters first; the symbols and the accented letters live behind the More key (RT does the same).
        private const int PageLetters = 0, PageSymbols = 1, PageAccents = 2;
        private const int ChipSlots = 5;
        private const double ChipSlotWidth = 246;
        private readonly List<List<List<Key>>> pages = new List<List<List<Key>>>();
        private readonly List<StackPanel> pagePanels = new List<StackPanel>();
        private List<List<Key>> rows;   // the open page's rows
        private int page;
        private readonly List<Border> chips = new List<Border>();          // permanent suggestion slots (placeholders included)
        private readonly List<string> chipText = new List<string>();       // real suggestions only
        private readonly List<TextBlock> chipLabels = new List<TextBlock>();
        private readonly RepeatLimiter limiter = new RepeatLimiter();
        private readonly Grid chipPanel = new Grid();
        private ScrollViewer textScroll;
        private TextBlock fieldText;
        private int lastRow = 1, lastCol;                                  // key to return to after picking a word
        private readonly TextBlock title;
        private readonly Run before = new Run(), caretRun = new Run("|"), after = new Run();
        private readonly TextBlock placeholder = Theme.Text("", 30, Theme.Ink60);   // the field's purpose, shown while it is empty
        private readonly DispatcherTimer blink = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        private bool caretOn = true;
        private Fader sheetFade;

        private string text = "";
        private int caret;
        private bool shift;
        private int row, col; // row -1 = suggestion chips
        private Action<string> done;
        private Action cancelled;
        private Func<string, IEnumerable<string>> suggest;

        // typing on a phone: the one phone link (PhoneRemote.cs) shows this keyboard on the phone page as soon as it opens, when the link is on;
        // View shows the QR card for pairing. One RemoteTyping while the keyboard is up.
        private RemoteTyping phone;
        private PhoneRemote phoneRemote;
        private Action phoneArrived;
        private bool secret, multiline, phoneConnected;
        private FrameworkElement phoneHost, phoneQrFrame;
        private Image phoneQr;
        private TextBlock phoneHead, phoneBody, phoneLink, phoneStatus;
        private Fader phoneFade;
        private readonly DispatcherTimer phoneNudge = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };

        public bool IsOpen { get { return Visibility == Visibility.Visible; } }

        public OnScreenKeyboard()
        {
            Visibility = Visibility.Collapsed;
            Focusable = false;
            limiter.FastAfter = 1000;   // hold a direction a second and cursor movement speeds up
            limiter.FastInterval = 55;
            BuildPages();
            rows = pages[PageLetters];

            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };

            // the typed-text field on top: what the field is for (also its placeholder), then a recessed pill with the text and
            // a blinking accent caret. Its height never changes - long text scrolls sideways so the sheet keeps its size.
            title = Theme.Title("", 26);
            title.MaxWidth = 300;
            title.Margin = new Thickness(8, 0, 22, 0);
            var tb = Theme.Text("", 30, Theme.Ink);
            fieldText = tb;
            tb.TextTrimming = TextTrimming.None;
            tb.TextWrapping = TextWrapping.NoWrap;
            caretRun.Foreground = Theme.Accent;
            caretRun.FontWeight = FontWeights.SemiBold;
            tb.Inlines.Add(before); tb.Inlines.Add(caretRun); tb.Inlines.Add(after);
            textScroll = new ScrollViewer
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = tb
            };
            placeholder.IsHitTestVisible = false;
            var textCell = new Grid();
            textCell.Children.Add(placeholder);
            textCell.Children.Add(textScroll);
            var fieldRow = new Grid();
            fieldRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            fieldRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            FrameworkElement lens = Theme.Icon(0xE8B6, 30, Theme.Ink60);   // search
            if (lens != null) { lens.Margin = new Thickness(0, 0, 16, 0); fieldRow.Children.Add(lens); }
            Grid.SetColumn(textCell, 1);
            fieldRow.Children.Add(textCell);
            var field = new Border
            {
                Background = Theme.ScrimA(0.4),
                BorderBrush = Theme.InkA(0.22),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(34),
                Padding = new Thickness(28, 0, 28, 1),
                Height = 68,
                ClipToBounds = true,
                Child = fieldRow
            };
            var head = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.Children.Add(title);
            Grid.SetColumn(field, 1);
            head.Children.Add(field);
            stack.Children.Add(head);

            // a permanently reserved suggestion strip (recent addresses, history matches). Fixed slots + dim placeholders mean the
            // layout never reflows when predictions appear or disappear.
            chipPanel.Height = 60;
            chipPanel.Margin = new Thickness(0, 0, 0, 6);
            chipPanel.HorizontalAlignment = HorizontalAlignment.Center;
            for (int i = 0; i < ChipSlots; i++)
            {
                chipPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ChipSlotWidth) });
                var chip = Theme.Tile("—", 22, 0xE889, Look.Pill);
                chip.Width = ChipSlotWidth - 8;
                chip.Height = 52;
                chip.Margin = new Thickness(4);
                chip.Padding = new Thickness(18, 0, 18, 1);
                var label = Theme.LabelOf(chip);
                if (label != null) label.MaxWidth = 150;
                Grid.SetColumn(chip, i);
                chipPanel.Children.Add(chip);
                chips.Add(chip);
                chipLabels.Add(label);
            }
            stack.Children.Add(chipPanel);

            // every page is built once and they share one grid cell (the sheet keeps the height of the tallest); only the open one shows
            var pageHost = new Grid();
            for (int p = 0; p < pages.Count; p++)
            {
                var panel = new StackPanel { Visibility = p == PageLetters ? Visibility.Visible : Visibility.Collapsed };
                foreach (var r in pages[p])
                {
                    var line = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                    foreach (var k in r)
                    {
                        // keys are 64 high with 26 pt letters; the wide ones (Shift, Space, Delete, More, Done) carry an icon and a word
                        int icon = 0;
                        switch (k.Kind)
                        {
                            case Kind.Shift: icon = 0xE5D8; break;   // arrow_upward
                            case Kind.Space: icon = 0xE256; break;   // space_bar
                            case Kind.Back: icon = 0xE14A; break;    // backspace
                            case Kind.Clear: icon = 0xE14C; break;   // clear
                            case Kind.Done: icon = 0xE5CA; break;    // check
                            case Kind.Page: if (k.Target == PageSymbols) icon = 0xE5D3; break;   // more_horiz
                        }
                        bool special = k.Kind != Kind.Char;
                        k.Box = Theme.Tile(special ? k.Lower : "", special ? 22 : 26, icon, Look.Key);
                        k.Box.Width = k.Width;
                        k.Box.Height = 64;
                        k.Box.Padding = new Thickness(0);
                        k.Box.Margin = new Thickness(4, 3, 4, 3);
                        k.Label = Theme.LabelOf(k.Box);
                        line.Children.Add(k.Box);
                    }
                    panel.Children.Add(line);
                }
                pagePanels.Add(panel);
                pageHost.Children.Add(panel);
            }
            stack.Children.Add(pageHost);
            // the button hints are the screen's own bottom bar (BrowserControl / YouTubeControl show OnScreenKeyboard.Hints there)

            // the sheet: glass, docked at the bottom of the screen over the dimmed page (scaled down only if the screen is short)
            var sheet = new Border
            {
                Width = 1304,
                Padding = new Thickness(36, 24, 36, 18),
                Child = stack,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom
            };
            Theme.Glass(sheet, 28);
            sheetFade = new Fader(sheet, 14);
            Content = new Grid
            {
                Background = Theme.ScrimA(0.5),
                Children =
                {
                    new Viewbox
                    {
                        Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
                        Margin = new Thickness(40, 24, 40, 16), Child = sheet
                    }
                }
            };
            ((Grid)Content).Children.Add(BuildPhoneCard());
            blink.Tick += delegate
            {
                caretOn = !caretOn;
                caretRun.Foreground = caretOn ? Theme.Accent : Brushes.Transparent;
            };
        }

        /// <summary>Shows the keyboard. done(text) on Done / Start; cancelled() on B. A on a suggestion inserts the word.
        /// suggest(text) is called on the UI thread after every edit and must be fast (history lookups, not network).
        /// secret = a password field (the phone keyboard asks for the Browser setting first); multiline = a text area (Enter on the phone adds a line).</summary>
        public void Open(string titleText, string initial, Action<string> done, Action cancelled, Func<string, IEnumerable<string>> suggest,
            bool secret = false, bool multiline = false)
        {
            ClosePhone();
            this.secret = secret; this.multiline = multiline;
            this.done = done; this.cancelled = cancelled; this.suggest = suggest;
            title.Text = titleText ?? "";
            placeholder.Text = titleText ?? "";
            text = initial ?? "";
            caret = text.Length;
            shift = false;
            ShowPage(PageLetters);
            row = 1; col = 0; lastRow = 1; lastCol = 0;
            Visibility = Visibility.Visible;
            sheetFade.Appear();
            Changed();
            // an already paired phone gets this keyboard on its page without anything pressed here
            if (BrowserSettings.PhoneRemote && BrowserSettings.PhoneKeyboard && (!secret || BrowserSettings.PhonePasswords) && RemoteService.Remote.Running) AttachPhone();
        }

        public bool HandlePad(string button)
        {
            if (!IsOpen) return false;
            switch (button)
            {
                case "DPadUp": if (limiter.Allow(button)) Move(-1, 0); break;
                case "DPadDown": if (limiter.Allow(button)) Move(1, 0); break;
                case "DPadLeft": if (limiter.Allow(button)) Move(0, -1); break;
                case "DPadRight": if (limiter.Allow(button)) Move(0, 1); break;
                case "A": if (limiter.Allow(button)) Press(); break;
                case "X": if (limiter.Allow(button)) Backspace(); break;
                case "Y": if (limiter.Allow(button)) Insert(" "); break;
                case "LeftShoulder": if (limiter.Allow(button)) { caret = Math.Max(0, caret - 1); Render(); } break;
                case "RightShoulder": if (limiter.Allow(button)) { caret = Math.Min(text.Length, caret + 1); Render(); } break;
                case "TriggerLeft": if (limiter.IsFirst(button) && page != PageSymbols) { shift = !shift; Refresh(); } break;
                case "TriggerRight": if (limiter.IsFirst(button)) SwitchPage(page == PageLetters ? PageSymbols : PageLetters); break;
                case "Back": if (limiter.IsFirst(button)) TogglePhone(); break;
                case "Start": if (limiter.IsFirst(button)) Finish(text); break;
                case "B": if (limiter.IsFirst(button)) Cancel(); break;
            }
            return true;
        }

        // ----- layout -----

        private void BuildPages()
        {
            // letters: a number row, three letter rows (Shift and Delete beside the last), and the bottom row like a phone's
            var letters = new List<List<Key>>();
            letters.Add(Chars("1234567890", false));
            letters.Add(Chars("qwertyuiop", true));
            letters.Add(Chars("asdfghjkl", true));
            var third = new List<Key> { Wide(Kind.Shift, "Shift", 150) };
            third.AddRange(Chars("zxcvbnm", true));
            third.Add(Wide(Kind.Back, "Delete", 150));
            letters.Add(third);
            letters.Add(Bottom(PageSymbols, "More"));
            pages.Add(letters);

            // More: the symbols a search box, a password or a URL needs
            var symbols = new List<List<Key>>();
            symbols.Add(Chars("1234567890", false));
            symbols.Add(Chars("@#$%&*-+()", false));
            symbols.Add(Chars("!?:;'\"/\\_=", false));
            var symThird = new List<Key> { new Key { Kind = Kind.Page, Lower = "éè", Upper = "éè", Width = 150, Target = PageAccents } };
            symThird.AddRange(Chars("~<>[]", false));
            symThird.Add(Wide(Kind.Clear, "Clear", 150));
            symThird.Add(Wide(Kind.Back, "Delete", 150));
            symbols.Add(symThird);
            symbols.Add(Bottom(PageLetters, "abc"));
            pages.Add(symbols);

            // accented letters (Shift = capitals)
            var accents = new List<List<Key>>();
            accents.Add(Accented("éèêëàâäùûü", "ÉÈÊËÀÂÄÙÛÜ"));
            accents.Add(Accented("ôöîïçñßœæø", "ÔÖÎÏÇÑßŒÆØ"));
            var accThird = new List<Key> { Wide(Kind.Shift, "Shift", 150) };
            accThird.AddRange(Accented("áíóúãõå", "ÁÍÓÚÃÕÅ"));
            accThird.Add(Wide(Kind.Back, "Delete", 150));
            accents.Add(accThird);
            accents.Add(Bottom(PageLetters, "abc"));
            pages.Add(accents);
        }

        private static Key Wide(Kind kind, string label, double width)
        {
            return new Key { Kind = kind, Lower = label, Upper = label, Width = width };
        }

        // the phone's bottom row: the page key, comma, a long space bar, full stop, .com and Done
        private List<Key> Bottom(int target, string label)
        {
            return new List<Key>
            {
                new Key { Kind = Kind.Page, Lower = label, Upper = label, Width = 150, Target = target },
                new Key { Lower = ",", Upper = "," },
                Wide(Kind.Space, "Space", 470),
                new Key { Lower = ".", Upper = "." },
                new Key { Lower = ".com", Upper = ".com", Width = 130 },
                Wide(Kind.Done, "Done", 170)
            };
        }

        private static List<Key> Accented(string lower, string upper)
        {
            var r = new List<Key>();
            for (int i = 0; i < lower.Length; i++) r.Add(new Key { Lower = lower[i].ToString(), Upper = upper[i].ToString() });
            return r;
        }

        private static List<Key> Chars(string chars, bool letters)
        {
            var r = new List<Key>();
            foreach (char c in chars)
                r.Add(new Key { Lower = c.ToString(), Upper = letters ? c.ToString().ToUpperInvariant() : c.ToString() });
            return r;
        }

        // shows a page without touching focus (Open) ...
        private void ShowPage(int p)
        {
            page = p;
            rows = pages[p];
            for (int i = 0; i < pagePanels.Count; i++) pagePanels[i].Visibility = i == p ? Visibility.Visible : Visibility.Collapsed;
        }

        // ... and a page switch from the keys: the old page's keys lose focus, the page key (bottom left) of the new one has it
        private void SwitchPage(int p)
        {
            foreach (var r in rows) foreach (var k in r) Theme.SetFocused(k.Box, false);
            ShowPage(p);
            row = rows.Count - 1;
            col = 0;
            lastRow = row; lastCol = col;
            Refresh();
        }

        private double CenterX(int r, int c)
        {
            double x = 0;
            for (int i = 0; i < c; i++) x += rows[r][i].Width + 8;
            return x + rows[r][c].Width / 2;
        }

        // ----- navigation -----

        private void Move(int dr, int dc)
        {
            if (row < 0)
            {
                // the suggestion strip is a 1D list: left/right wrap, up clamps at the top
                if (dc != 0 && chipText.Count > 0) col = (col + dc + chipText.Count) % chipText.Count;
                else if (dr > 0) { row = lastRow; col = lastCol; }   // back to the key we came from
                Refresh();
                return;
            }
            if (dc != 0)
            {
                col = (col + dc + rows[row].Count) % rows[row].Count;
                Refresh();
                return;
            }
            double x = CenterX(row, col);
            int nr = row + dr;
            // the grid is 2D: clamp at both edges instead of jumping across the whole keyboard
            if (nr < -1) nr = -1;
            else if (nr >= rows.Count) nr = rows.Count - 1;
            if (nr == row) return;
            if (nr == -1)
            {
                lastRow = row; lastCol = col;
                row = -1; col = 0;
            }
            else
            {
                row = nr;
                int best = 0; double bd = double.MaxValue;
                for (int i = 0; i < rows[row].Count; i++)
                {
                    double d = Math.Abs(CenterX(row, i) - x);
                    if (d < bd) { bd = d; best = i; }
                }
                col = best;
            }
            Refresh();
        }

        // ----- editing -----

        private void Press()
        {
            if (row < 0)
            {
                // insert the word with a trailing space and return focus to the key we left
                if (col < chipText.Count)
                {
                    string word = chipText[col];
                    row = lastRow; col = lastCol;
                    Insert(word + " ");
                }
                return;
            }
            var k = rows[row][col];
            switch (k.Kind)
            {
                case Kind.Shift: shift = !shift; Refresh(); break;
                case Kind.Space: Insert(" "); break;
                case Kind.Back: Backspace(); break;
                case Kind.Clear: text = ""; caret = 0; Changed(); break;
                case Kind.Page: SwitchPage(k.Target); break;
                case Kind.Done: Finish(text); break;
                default:
                    Insert(shift ? k.Upper : k.Lower);
                    if (shift) { shift = false; Refresh(); }
                    break;
            }
        }

        private void Insert(string s)
        {
            text = text.Insert(caret, s);
            caret += s.Length;
            Changed();
        }

        private void Backspace()
        {
            if (caret == 0) return;
            text = text.Remove(caret - 1, 1);
            caret--;
            Changed();
        }

        private void Finish(string result)
        {
            ClosePhone();
            blink.Stop();
            Visibility = Visibility.Collapsed;
            if (done != null) done(result);
        }

        private void Cancel()
        {
            ClosePhone();
            blink.Stop();
            Visibility = Visibility.Collapsed;
            if (cancelled != null) cancelled();
        }

        // ----- typing on a phone (View) -----

        private FrameworkElement BuildPhoneCard()
        {
            phoneQr = new Image { Width = 300, Height = 300, Stretch = Stretch.Uniform };
            phoneQrFrame = new Border
            {
                Background = Brushes.White, CornerRadius = new CornerRadius(16), Padding = new Thickness(4), Child = phoneQr,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 32, 0)
            };
            phoneHead = Theme.Title("Type on your phone", 34);
            phoneHead.Margin = new Thickness(0, 0, 0, 10);
            phoneBody = Wrapped(Theme.Text("", 23, Theme.Ink));
            phoneLink = Wrapped(Theme.Text("", 19, Theme.Ink60));
            phoneLink.Margin = new Thickness(0, 12, 0, 0);
            phoneStatus = Wrapped(Theme.Text("", 21, Theme.Accent));
            phoneStatus.Margin = new Thickness(0, 14, 0, 0);
            var close = Theme.Text("View  Close this     B  Cancel typing", 19, Theme.Ink60);
            close.Margin = new Thickness(0, 16, 0, 0);
            var info = new StackPanel { Width = 470, VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(phoneHead);
            info.Children.Add(phoneBody);
            info.Children.Add(phoneLink);
            info.Children.Add(phoneStatus);
            info.Children.Add(close);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(phoneQrFrame);
            row.Children.Add(info);
            var card = new Border { Padding = new Thickness(36, 32, 40, 32), Child = row };
            Theme.Glass(card, 30);
            phoneFade = new Fader(card, 12);
            phoneHost = new Viewbox
            {
                Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(40, 56, 40, 0), Child = card, Visibility = Visibility.Collapsed
            };
            phoneNudge.Tick += delegate
            {
                phoneNudge.Stop();
                if (phone != null && !phoneConnected)
                    SetPhoneStatus("Still nothing? Check that your phone is on the same Wi-Fi as this PC, not on mobile data.");
            };
            return phoneHost;
        }

        private static TextBlock Wrapped(TextBlock t)
        {
            t.TextWrapping = TextWrapping.Wrap;
            t.TextTrimming = TextTrimming.None;
            return t;
        }

        private void SetPhoneStatus(string s) { if (phoneStatus.Text != s) phoneStatus.Text = s; }

        private static string AccentHex()
        {
            var b = Theme.Accent as SolidColorBrush;
            if (b == null) return "#7aa2ff";
            Color c = b.Color;
            return "#" + c.R.ToString("x2") + c.G.ToString("x2") + c.B.ToString("x2");
        }

        // a message instead of the code (the feature is off, a password, no network)
        private void ShowPhoneNote(string head, string body)
        {
            phoneHead.Text = head;
            phoneBody.Text = body;
            phoneLink.Text = "";
            phoneStatus.Text = "";
            phoneQrFrame.Visibility = Visibility.Collapsed;
            phoneHost.Visibility = Visibility.Visible;
            phoneFade.Appear();
        }

        private void Ui(Action a)
        {
            try { Dispatcher.BeginInvoke(a); } catch { }
        }

        // the keyboard joins the phone link: the phone page shows a typing box for it
        private void AttachPhone()
        {
            if (phone != null && !phone.IsClosed) return;
            PhoneRemote r = RemoteService.Remote;
            RemoteTyping s = r.BeginTyping(title.Text, text, multiline, secret, AccentHex());
            phone = s;
            phoneRemote = r;
            s.TextFromPhone += delegate(string t)
            {
                Ui(delegate
                {
                    if (phone != s || !IsOpen) return;
                    PadActivity.Mark();   // the pad is idle while you type on the phone: keep the controller-rest timer away
                    text = t;
                    caret = t.Length;
                    Changed();
                });
            };
            s.Done += delegate { Ui(delegate { if (phone == s && IsOpen) Finish(text); }); };
            s.Cancelled += delegate { Ui(delegate { if (phone == s && IsOpen) Cancel(); }); };
            phoneArrived = delegate { Ui(delegate { if (phone == s) { phoneConnected = true; SetPhoneStatus("Phone connected. Type there or here: both work."); } }); };
            r.PhoneConnected += phoneArrived;
        }

        private void TogglePhone()
        {
            if (phoneHost.Visibility == Visibility.Visible) { phoneNudge.Stop(); phoneHost.Visibility = Visibility.Collapsed; return; }
            if (!BrowserSettings.PhoneKeyboard) { ShowPhoneNote("Phone keyboard is off", "Turn it on in the browser's Settings (Start menu > Settings)."); return; }
            if (secret && !BrowserSettings.PhonePasswords)
            {
                ShowPhoneNote("Type passwords on the TV", "A password typed on a phone would cross your Wi-Fi unencrypted. You can allow it in the browser's Settings (Start menu > Settings).");
                return;
            }
            string error = RemoteService.Enable();   // pressing View is the user's yes: the phone link switches on (and stays on)
            if (error != null) { ShowPhoneNote("Phone link unavailable", error); return; }
            PhoneRemote r = RemoteService.Remote;
            string url = r.Url;
            if (url == null) { ShowPhoneNote("Phone link unavailable", "It could not start."); return; }
            try { phoneQr.Source = new QrCode(url).Picture(); }
            catch (Exception ex)
            {
                ShowPhoneNote("Phone link unavailable", "The code could not be drawn: " + ex.Message);
                return;
            }
            AttachPhone();
            RemoteTyping s = phone;
            phoneConnected = r.PhoneHere;
            phoneHead.Text = "Type on your phone";
            phoneBody.Text = "Scan the code once with your phone's camera (same Wi-Fi as this PC). It is the same page for the keyboard, the browser and YouTube. After that, the keyboard shows up on the phone by itself.";
            phoneLink.Text = url.Replace("http://", "");
            SetPhoneStatus(phoneConnected ? "Phone connected. Type there or here: both work." : "Waiting for your phone...");
            phoneQrFrame.Visibility = Visibility.Visible;
            phoneHost.Visibility = Visibility.Visible;
            phoneFade.Appear();
            phoneNudge.Stop();
            if (!phoneConnected) phoneNudge.Start();
            PhoneFirewall.CheckAsync(delegate(FirewallState state)
            {
                if (state != FirewallState.Missing && state != FirewallState.Blocked) return;
                Ui(delegate
                {
                    if (phone != s || phoneConnected) return;
                    SetPhoneStatus("Windows Firewall may stop your phone from connecting. Set it up once in the browser's Settings (needs a mouse or keyboard).");
                });
            });
        }

        // the keyboard is done (or opens again): the phone page drops its typing box
        private void ClosePhone()
        {
            phoneNudge.Stop();
            RemoteTyping s = phone;
            phone = null;
            if (phoneRemote != null && phoneArrived != null) phoneRemote.PhoneConnected -= phoneArrived;
            phoneArrived = null;
            phoneRemote = null;
            if (s != null) s.Close();
            if (phoneHost != null) phoneHost.Visibility = Visibility.Collapsed;
        }

        // Text changed: refill the fixed suggestion slots, then redraw everything. The strip itself
        // is never hidden or resized - only its contents change - so the card's geometry is stable.
        private void Changed()
        {
            chipText.Clear();
            if (suggest != null)
            {
                try
                {
                    foreach (var s in (suggest(text) ?? new string[0]).Where(x => !string.IsNullOrEmpty(x)).Take(ChipSlots))
                        chipText.Add(s);
                }
                catch { }
            }
            for (int i = 0; i < chips.Count; i++)
            {
                bool has = i < chipText.Count;
                if (chipLabels[i] != null) chipLabels[i].Text = has ? chipText[i] : "—";
                chips[i].Opacity = has ? 1 : 0.35;
            }
            if (row < 0 && chipText.Count == 0) { row = 1; col = 0; }
            if (row < 0 && col >= chipText.Count) col = 0;
            if (phone != null) phone.SetText(text);   // the phone's field follows the pad's typing (a no-op when the text came from it)
            Render();
            Refresh();
        }

        private void Render()
        {
            before.Text = text.Substring(0, caret);
            after.Text = text.Substring(caret);
            placeholder.Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            // the caret stays solid while the text is being edited, then blinks again
            caretOn = true;
            caretRun.Foreground = Theme.Accent;
            blink.Stop();
            blink.Start();
            if (textScroll == null || fieldText == null) return;
            textScroll.UpdateLayout();
            double x;
            try
            {
                var face = new Typeface(fieldText.FontFamily, fieldText.FontStyle, fieldText.FontWeight, fieldText.FontStretch);
                var measured = new FormattedText(before.Text, System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, face, fieldText.FontSize, Theme.Ink, 1.0);
                x = measured.Width;
            }
            catch { x = 0; }
            double caretWidth = fieldText.FontSize * 0.6;
            double view = textScroll.ViewportWidth;
            if (x < textScroll.HorizontalOffset)
                textScroll.ScrollToHorizontalOffset(Math.Max(0, x - 20));
            else if (x + caretWidth > textScroll.HorizontalOffset + view - 20)
                textScroll.ScrollToHorizontalOffset(x + caretWidth - view + 40);
        }

        private void Refresh()
        {
            for (int r = 0; r < rows.Count; r++)
                for (int c = 0; c < rows[r].Count; c++)
                {
                    var k = rows[r][c];
                    k.Label.Text = shift ? k.Upper : k.Lower;
                    Theme.SetToggled(k.Box, k.Kind == Kind.Shift && shift);
                    Theme.SetSelected(k.Box, k.Kind == Kind.Done);   // the way out stands out a little
                    Theme.SetFocused(k.Box, r == row && c == col);
                }
            for (int i = 0; i < chips.Count; i++)
                Theme.SetFocused(chips[i], row < 0 && i == col && i < chipText.Count);
        }
    }
}
