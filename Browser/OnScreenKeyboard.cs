// OnScreenKeyboard.cs - pad-driven on-screen keyboard overlay (typing URLs, search boxes, form fields).
// Put it on top of the page in the same Grid cell; it stays collapsed until Open(). While IsOpen the host
// must route every pad button to HandlePad (it is modal and always consumes).
//   D-pad move (wraps)  A type  X backspace  Y space  LB/RB move text cursor  LT shift  Start done  B cancel
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace SeleneBrowser
{
    public class OnScreenKeyboard : UserControl
    {
        private enum Kind { Char, Shift, Space, Back, Clear, Done }

        private sealed class Key
        {
            public Kind Kind;
            public string Lower, Upper;
            public double Width = 82;
            public Border Box;
            public TextBlock Label;
        }

        public const string Hints = "A Type    X Delete    Y Space    LB/RB Cursor    LT Shift    Start Done    B Cancel";

        private const string ShiftedDigits = "!@#$%^&*()";
        private readonly List<List<Key>> rows = new List<List<Key>>();
        private readonly List<Border> chips = new List<Border>();
        private readonly List<string> chipText = new List<string>();
        private readonly RepeatLimiter limiter = new RepeatLimiter();
        private readonly WrapPanel chipPanel = new WrapPanel();
        private readonly TextBlock title;
        private readonly Run before = new Run(), caretRun = new Run("|"), after = new Run();

        private string text = "";
        private int caret;
        private bool shift;
        private int row, col; // row -1 = suggestion chips
        private Action<string> done;
        private Action cancelled;
        private Func<string, IEnumerable<string>> suggest;

        public bool IsOpen { get { return Visibility == Visibility.Visible; } }

        public OnScreenKeyboard()
        {
            Visibility = Visibility.Collapsed;
            Focusable = false;
            BuildRows();

            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            title = Theme.Text("", 28, Theme.Ink60);
            title.Margin = new Thickness(6, 0, 0, 10);
            stack.Children.Add(title);

            var tb = Theme.Text("", 36, Theme.Ink);
            tb.TextTrimming = TextTrimming.None;
            tb.TextWrapping = TextWrapping.Wrap;
            caretRun.Foreground = Theme.Accent;
            tb.Inlines.Add(before); tb.Inlines.Add(caretRun); tb.Inlines.Add(after);
            stack.Children.Add(new Border
            {
                Background = Theme.Background,
                BorderBrush = Theme.Ink60,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(18, 10, 18, 10),
                MinHeight = 64,
                Margin = new Thickness(0, 0, 0, 10),
                Child = tb
            });
            stack.Children.Add(chipPanel);

            foreach (var r in rows)
            {
                var line = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                foreach (var k in r)
                {
                    k.Box = Theme.Tile("", 30);
                    k.Box.Width = k.Width;
                    k.Box.Height = 72;
                    k.Box.Padding = new Thickness(0);
                    k.Label = (TextBlock)k.Box.Child;
                    line.Children.Add(k.Box);
                }
                stack.Children.Add(line);
            }
            var hint = Theme.Text(Hints, 22, Theme.Ink60);
            hint.Margin = new Thickness(6, 12, 0, 0);
            hint.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(hint);

            var panel = new Border
            {
                Background = Theme.Panel,
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(30),
                Child = stack,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Content = new Grid
            {
                Background = new SolidColorBrush(Color.FromArgb(0xCC, 0, 0, 0)),
                Children = { new Viewbox { Stretch = Stretch.Uniform, Margin = new Thickness(40), Child = panel } }
            };
        }

        /// <summary>Shows the keyboard. done(text) on Done or when a suggestion is picked; cancelled() on B.
        /// suggest(text) is called on the UI thread after every edit and must be fast (history lookups, not network).</summary>
        public void Open(string titleText, string initial, Action<string> done, Action cancelled, Func<string, IEnumerable<string>> suggest)
        {
            this.done = done; this.cancelled = cancelled; this.suggest = suggest;
            title.Text = titleText ?? "";
            text = initial ?? "";
            caret = text.Length;
            shift = false;
            row = 1; col = 0;
            Visibility = Visibility.Visible;
            Changed();
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
                case "TriggerLeft": if (limiter.IsFirst(button)) { shift = !shift; Refresh(); } break;
                case "Start": if (limiter.IsFirst(button)) Finish(text); break;
                case "B": if (limiter.IsFirst(button)) Cancel(); break;
            }
            return true;
        }

        // ----- layout -----

        private void BuildRows()
        {
            AddChars("1234567890");
            AddChars("qwertyuiop");
            AddChars("asdfghjkl");
            AddChars("zxcvbnm,.");
            var r = new List<Key> { new Key { Kind = Kind.Shift, Lower = "Shift", Upper = "Shift", Width = 130 } };
            r.Add(new Key { Lower = ".com", Upper = ".com", Width = 130 });
            foreach (char c in "/:-_@?") r.Add(new Key { Lower = c.ToString(), Upper = c.ToString() });
            rows.Add(r);
            rows.Add(new List<Key>
            {
                new Key { Kind = Kind.Space, Lower = "Space", Upper = "Space", Width = 360 },
                new Key { Kind = Kind.Back, Lower = "Delete", Upper = "Delete", Width = 160 },
                new Key { Kind = Kind.Clear, Lower = "Clear", Upper = "Clear", Width = 160 },
                new Key { Kind = Kind.Done, Lower = "Done", Upper = "Done", Width = 160 }
            });
        }

        private void AddChars(string chars)
        {
            var r = new List<Key>();
            foreach (char c in chars)
            {
                string up = char.IsDigit(c) ? ShiftedDigits[(c - '0' + 9) % 10].ToString() : c.ToString().ToUpperInvariant();
                r.Add(new Key { Lower = c.ToString(), Upper = up });
            }
            rows.Add(r);
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
                if (dc != 0) col = (col + dc + chips.Count) % chips.Count;
                else if (dr > 0) { row = 0; col = 0; }
                else { row = rows.Count - 1; col = 0; }
            }
            else if (dc != 0) col = (col + dc + rows[row].Count) % rows[row].Count;
            else
            {
                double x = CenterX(row, col);
                int nr = row + dr;
                if (nr < 0) nr = chips.Count > 0 ? -1 : rows.Count - 1;
                else if (nr >= rows.Count) nr = chips.Count > 0 ? -1 : 0;
                row = nr;
                if (row < 0) col = 0;
                else
                {
                    int best = 0; double bd = double.MaxValue;
                    for (int i = 0; i < rows[row].Count; i++)
                    {
                        double d = Math.Abs(CenterX(row, i) - x);
                        if (d < bd) { bd = d; best = i; }
                    }
                    col = best;
                }
            }
            Refresh();
        }

        // ----- editing -----

        private void Press()
        {
            if (row < 0) { if (col < chipText.Count) Finish(chipText[col]); return; }
            var k = rows[row][col];
            switch (k.Kind)
            {
                case Kind.Shift: shift = !shift; Refresh(); break;
                case Kind.Space: Insert(" "); break;
                case Kind.Back: Backspace(); break;
                case Kind.Clear: text = ""; caret = 0; Changed(); break;
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
            Visibility = Visibility.Collapsed;
            if (done != null) done(result);
        }

        private void Cancel()
        {
            Visibility = Visibility.Collapsed;
            if (cancelled != null) cancelled();
        }

        // Text changed: refresh suggestions, then redraw everything.
        private void Changed()
        {
            chips.Clear(); chipText.Clear(); chipPanel.Children.Clear();
            if (suggest != null)
            {
                try
                {
                    foreach (var s in (suggest(text) ?? new string[0]).Where(x => !string.IsNullOrEmpty(x)).Take(5))
                    {
                        var chip = Theme.Tile(s, 24);
                        chip.MaxWidth = 520;
                        chips.Add(chip); chipText.Add(s); chipPanel.Children.Add(chip);
                    }
                }
                catch { }
            }
            chipPanel.Visibility = chips.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (row < 0 && chips.Count == 0) { row = 1; col = 0; }
            if (row < 0 && col >= chips.Count) col = 0;
            Render();
            Refresh();
        }

        private void Render()
        {
            before.Text = text.Substring(0, caret);
            after.Text = text.Substring(caret);
        }

        private void Refresh()
        {
            for (int r = 0; r < rows.Count; r++)
                for (int c = 0; c < rows[r].Count; c++)
                {
                    var k = rows[r][c];
                    k.Label.Text = shift ? k.Upper : k.Lower;
                    Theme.SetFocused(k.Box, r == row && c == col);
                    k.Box.Background = k.Kind == Kind.Shift && shift ? Theme.Ink60 : Theme.Key;
                }
            for (int i = 0; i < chips.Count; i++) Theme.SetFocused(chips[i], row < 0 && i == col);
        }
    }
}
