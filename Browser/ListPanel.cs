// ListPanel.cs - one glass card with a title, an optional note and a list of pill rows (a word on the left, a smaller one on the right),
// driven by the pad. The browser uses it for History, Downloads and the question a download asks. Built from the Theme helpers like the
// Start menu and the settings card. C# 5 only.
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace SeleneBrowser
{
    internal sealed class ListPanel : Grid
    {
        internal sealed class Row
        {
            public string Title, Sub;
            public int Icon;
            public object Tag;
            internal Border Tile;
            internal TextBlock SubText;
        }

        private const int Visible = 7;
        private readonly RepeatLimiter limiter;
        private readonly Border card = new Border();
        private readonly TextBlock titleText = Theme.Title("", 30);
        private readonly TextBlock noteText = Theme.Text("", 21, Theme.Ink60);
        private readonly StackPanel list = new StackPanel();
        private readonly TextBlock footer = Theme.Text("", 19, Theme.Ink60);
        private readonly Fader fade;
        private readonly List<Row> rows = new List<Row>();
        private int index, top;
        private string empty = "";

        /// <summary>A on a row, X on a row, Y on a row (the host decides what they mean), B / Start to leave.</summary>
        public Action<int> Chosen, Secondary, Tertiary;
        /// <summary>The hint line the host shows while the panel is open.</summary>
        public string Hints = "D-pad Select    A Choose    B Close";

        public bool IsOpen { get { return Visibility == Visibility.Visible; } }
        public int Index { get { return index; } }
        public int Count { get { return rows.Count; } }

        public ListPanel(RepeatLimiter limiter)
        {
            this.limiter = limiter;
            Background = Theme.ScrimA(0.62);
            Visibility = Visibility.Collapsed;
            Theme.Glass(card, 28);
            card.Padding = new Thickness(24, 26, 24, 22);
            card.Width = 980;
            card.HorizontalAlignment = HorizontalAlignment.Center;
            card.VerticalAlignment = VerticalAlignment.Center;
            var stack = new StackPanel();
            titleText.Margin = new Thickness(26, 0, 26, 6);
            stack.Children.Add(titleText);
            noteText.TextWrapping = TextWrapping.Wrap;
            noteText.TextTrimming = TextTrimming.None;
            noteText.Margin = new Thickness(28, 0, 28, 12);
            stack.Children.Add(noteText);
            stack.Children.Add(list);
            footer.Margin = new Thickness(28, 10, 28, 0);
            stack.Children.Add(footer);
            card.Child = stack;
            fade = new Fader(card, 10);
            Children.Add(new Viewbox { Stretch = System.Windows.Media.Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(40, 24, 40, 24), Child = card });
        }

        /// <summary>Opens the card. emptyText shows when there are no rows.</summary>
        public void Open(string title, string note, List<Row> items, string emptyText, string hints)
        {
            titleText.Text = title ?? "";
            noteText.Text = note ?? "";
            noteText.Visibility = string.IsNullOrEmpty(note) ? Visibility.Collapsed : Visibility.Visible;
            empty = emptyText ?? "";
            Hints = hints ?? Hints;
            SetRows(items, 0);
            Visibility = Visibility.Visible;
            fade.Appear();
        }

        public void Close() { Visibility = Visibility.Collapsed; }

        /// <summary>Replaces the rows (after one was removed), keeping the selection near where it was.</summary>
        public void SetRows(List<Row> items, int selected)
        {
            rows.Clear();
            if (items != null) rows.AddRange(items);
            index = rows.Count == 0 ? 0 : Math.Max(0, Math.Min(rows.Count - 1, selected));
            top = Math.Max(0, Math.Min(index, Math.Max(0, rows.Count - Visible)));
            Rebuild();
        }

        /// <summary>The words on the rows changed (a download's progress): redraw them without moving.</summary>
        public void Refresh()
        {
            foreach (Row r in rows) if (r.SubText != null && r.SubText.Text != (r.Sub ?? "")) r.SubText.Text = r.Sub ?? "";
            Paint();
        }

        private void Rebuild()
        {
            list.Children.Clear();
            if (rows.Count == 0)
            {
                var none = Theme.Text(empty, 24, Theme.Ink60);
                none.Margin = new Thickness(28, 18, 28, 18);
                none.TextWrapping = TextWrapping.Wrap;
                list.Children.Add(none);
                footer.Text = "";
                return;
            }
            if (index < top) top = index;
            if (index >= top + Visible) top = index - Visible + 1;
            for (int i = top; i < Math.Min(rows.Count, top + Visible); i++) list.Children.Add(Build(rows[i]));
            footer.Text = rows.Count > Visible ? (index + 1) + " / " + rows.Count : "";
            Paint();
        }

        private FrameworkElement Build(Row r)
        {
            var tile = Theme.Tile(r.Title ?? "", 24, r.Icon, Look.Pill);
            tile.Height = 60;
            tile.Padding = new Thickness(26, 0, 300, 1);
            tile.Margin = new Thickness(0, 3, 0, 3);
            Theme.AlignLeft(tile);
            Theme.SetFocusScale(tile, 1.02);
            TextBlock label = Theme.LabelOf(tile);
            if (label != null) label.MaxWidth = 560;
            var sub = Theme.Text(r.Sub ?? "", 19, Theme.Ink60);
            sub.HorizontalAlignment = HorizontalAlignment.Right;
            sub.Margin = new Thickness(0, 0, 28, 0);
            sub.MaxWidth = 270;
            sub.IsHitTestVisible = false;
            Panel.SetZIndex(sub, 5);
            r.Tile = tile;
            r.SubText = sub;
            var g = new Grid();
            g.Children.Add(tile);
            g.Children.Add(sub);
            return g;
        }

        private void Paint()
        {
            for (int i = 0; i < rows.Count; i++)
            {
                Row r = rows[i];
                if (r.Tile == null) continue;
                bool focused = i == index;
                Theme.SetFocused(r.Tile, focused);
                if (r.SubText != null) r.SubText.Foreground = focused ? Theme.OnAccent : Theme.Ink60;
            }
        }

        /// <summary>Pad input while open. Returns true when the panel asks to be closed.</summary>
        public bool HandlePad(string b)
        {
            switch (b)
            {
                case "DPadUp": if (limiter.Allow(b) && rows.Count > 0) { index = (index + rows.Count - 1) % rows.Count; Rebuild(); } break;
                case "DPadDown": if (limiter.Allow(b) && rows.Count > 0) { index = (index + 1) % rows.Count; Rebuild(); } break;
                case "A": if (limiter.IsFirst(b) && rows.Count > 0 && Chosen != null) Chosen(index); break;
                case "X": if (limiter.IsFirst(b) && rows.Count > 0 && Secondary != null) Secondary(index); break;
                case "Y": if (limiter.IsFirst(b) && rows.Count > 0 && Tertiary != null) Tertiary(index); break;
                case "B": case "Start": return limiter.IsFirst(b);
            }
            return false;
        }
    }
}
