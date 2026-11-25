// TabSwitcher.cs - all the browser's tabs at once, as a grid of cards with a picture of each page (like a console's task switcher):
// D-pad moves, A goes to the tab, X closes it, Y opens a new one, B / Start leaves. Built from the Theme helpers. C# 5 only.
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace SeleneBrowser
{
    internal sealed class TabSwitcher : Grid
    {
        internal sealed class TabCard
        {
            public string Title, Sub;
            public ImageSource Thumb;
            public bool Current;
            internal Border Card;
        }

        private const int Columns = 3;
        private const double CardWidth = 400, CardHeight = 290;
        private readonly RepeatLimiter limiter;
        private readonly Border box = new Border();
        private readonly TextBlock titleText = Theme.Title("All tabs", 30);
        private readonly UniformGrid grid = new UniformGrid { Columns = Columns };
        private readonly Fader fade;
        private readonly List<TabCard> cards = new List<TabCard>();
        private Border addCard;
        private int index;   // cards.Count = the New tab card

        /// <summary>A on a tab, X on a tab (close it), Y or A on the last card (a new tab).</summary>
        public Action<int> Chosen, CloseRequested;
        public Action NewTab;
        public bool CanAdd = true;
        public const string Hints = "D-pad Select    A Open    X Close tab    Y New tab    B Back";

        public bool IsOpen { get { return Visibility == Visibility.Visible; } }
        public int Index { get { return index; } }

        public TabSwitcher(RepeatLimiter limiter)
        {
            this.limiter = limiter;
            Background = Theme.ScrimA(0.7);
            Visibility = Visibility.Collapsed;
            Theme.Glass(box, 30);
            box.Padding = new Thickness(26, 26, 26, 20);
            box.HorizontalAlignment = HorizontalAlignment.Center;
            box.VerticalAlignment = VerticalAlignment.Center;
            var stack = new StackPanel();
            titleText.Margin = new Thickness(14, 0, 14, 12);
            stack.Children.Add(titleText);
            stack.Children.Add(grid);
            box.Child = stack;
            fade = new Fader(box, 10);
            Children.Add(new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(40, 24, 40, 24), Child = box });
        }

        public void Open(List<TabCard> items, int selected, bool canAdd)
        {
            cards.Clear();
            cards.AddRange(items);
            CanAdd = canAdd;
            index = Math.Max(0, Math.Min(Total() - 1, selected));
            Rebuild();
            Visibility = Visibility.Visible;
            fade.Appear();
        }

        public void Close() { Visibility = Visibility.Collapsed; }

        private int Total() { return cards.Count + (CanAdd ? 1 : 0); }

        private void Rebuild()
        {
            grid.Children.Clear();
            foreach (TabCard c in cards) grid.Children.Add(BuildCard(c));
            addCard = null;
            if (CanAdd)
            {
                Grid face;
                addCard = Theme.Card(CardWidth, CardHeight, 20, out face);
                var plus = Theme.Icon(0xE145, 64, Theme.Ink60);
                if (plus != null) { plus.HorizontalAlignment = HorizontalAlignment.Center; plus.VerticalAlignment = VerticalAlignment.Center; plus.Margin = new Thickness(0, 0, 0, 28); face.Children.Add(plus); }
                var label = Theme.Text("New tab", 24, Theme.Ink);
                label.HorizontalAlignment = HorizontalAlignment.Center;
                label.VerticalAlignment = VerticalAlignment.Bottom;
                label.Margin = new Thickness(0, 0, 0, 26);
                face.Children.Add(label);
                addCard.Margin = new Thickness(6);
                grid.Children.Add(addCard);
            }
            Paint();
        }

        private FrameworkElement BuildCard(TabCard c)
        {
            Grid face;
            Border card = Theme.Card(CardWidth, CardHeight, 20, out face);
            card.Margin = new Thickness(6);
            c.Card = card;
            if (c.Thumb != null)
                face.Children.Add(new Image { Source = c.Thumb, Stretch = Stretch.UniformToFill, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center, Height = CardHeight - 74 });
            else
            {
                var globe = Theme.Icon(0xE80B, 56, Theme.InkA(0.35));   // public
                if (globe != null) { globe.HorizontalAlignment = HorizontalAlignment.Center; globe.VerticalAlignment = VerticalAlignment.Center; globe.Margin = new Thickness(0, 0, 0, 60); face.Children.Add(globe); }
            }
            // the words sit on a darker strip along the bottom
            var strip = new Border { Background = Theme.GlassA(0.92), VerticalAlignment = VerticalAlignment.Bottom, Height = 74, Padding = new Thickness(18, 8, 18, 8) };
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var name = Theme.Text(string.IsNullOrEmpty(c.Title) ? "New tab" : c.Title, 22, Theme.Ink);
            name.FontWeight = FontWeights.SemiBold;
            name.MaxWidth = CardWidth - 60;
            var sub = Theme.Text(c.Sub ?? "", 17, Theme.Ink60);
            sub.MaxWidth = CardWidth - 60;
            words.Children.Add(name);
            words.Children.Add(sub);
            strip.Child = words;
            face.Children.Add(strip);
            if (c.Current)
            {
                var chip = Theme.Chip("OPEN", Theme.Accent, Theme.OnAccent, 15);
                chip.HorizontalAlignment = HorizontalAlignment.Right;
                chip.VerticalAlignment = VerticalAlignment.Top;
                chip.Margin = new Thickness(0, 12, 12, 0);
                face.Children.Add(chip);
            }
            return card;
        }

        private void Paint()
        {
            for (int i = 0; i < cards.Count; i++) Theme.SetFocused(cards[i].Card, i == index);
            if (addCard != null) Theme.SetFocused(addCard, index == cards.Count);
        }

        private void Step(int dx, int dy)
        {
            int total = Total();
            if (total == 0) return;
            int col = index % Columns, row = index / Columns;
            if (dx != 0) { index = (index + dx + total) % total; }
            else
            {
                int rows = (total + Columns - 1) / Columns;
                int next = row + dy;
                if (next < 0 || next >= rows) return;
                int at = next * Columns + col;
                index = Math.Min(at, total - 1);
            }
            Paint();
        }

        /// <summary>Pad input while open. Returns true when the switcher asks to be closed.</summary>
        public bool HandlePad(string b)
        {
            switch (b)
            {
                case "DPadLeft": if (limiter.Allow(b)) Step(-1, 0); break;
                case "DPadRight": if (limiter.Allow(b)) Step(1, 0); break;
                case "DPadUp": if (limiter.Allow(b)) Step(0, -1); break;
                case "DPadDown": if (limiter.Allow(b)) Step(0, 1); break;
                case "A":
                    if (!limiter.IsFirst(b)) break;
                    if (index >= cards.Count) { if (NewTab != null) NewTab(); }
                    else if (Chosen != null) Chosen(index);
                    break;
                case "X": if (limiter.IsFirst(b) && index < cards.Count && CloseRequested != null) CloseRequested(index); break;
                case "Y": if (limiter.IsFirst(b) && CanAdd && NewTab != null) NewTab(); break;
                case "B": case "Start": return limiter.IsFirst(b);
            }
            return false;
        }
    }
}
