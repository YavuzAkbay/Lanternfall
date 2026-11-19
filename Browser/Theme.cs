// Theme.cs - brush lookup (Selene palette with fallbacks), focus visuals and shared fonts.
// SeleneBrowser.dll cannot reference SeleneCore, so the palette is read at runtime from the
// application resources ("SeleneInkBrush", ...). Every lookup has a hard-coded fallback.
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SeleneBrowser
{
    internal static class Theme
    {
        public static readonly FontFamily Font = new FontFamily("Segoe UI");

        /// <summary>Palette brush by resource key, or a frozen solid brush of the fallback color.</summary>
        public static Brush Get(string key, string fallbackHex)
        {
            try
            {
                var app = Application.Current;
                if (app != null)
                {
                    var b = app.TryFindResource(key) as Brush;
                    if (b != null) return b;
                }
            }
            catch { }
            var fb = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallbackHex));
            fb.Freeze();
            return fb;
        }

        public static Brush Ink { get { return Get("SeleneInkBrush", "#F2F4F8"); } }
        public static Brush Ink60 { get { return Get("SeleneInk60Brush", "#9AA3B5"); } }
        public static Brush Accent { get { return Get("SeleneFocusBrush", "#00E5FF"); } }
        public static Brush Background { get { return Get("SeleneBaseBrush", "#0B0E14"); } }
        public static Brush Panel { get { return Get("SeleneGlassSolidBrush", "#161B27"); } }
        public static Brush Key { get { return Get("SeleneGlassLightBrush", "#242C3F"); } }

        /// <summary>Accent color as #RRGGBB (for injected page CSS).</summary>
        public static string AccentHex
        {
            get
            {
                var s = Accent as SolidColorBrush;
                if (s == null) return "#00E5FF";
                return "#" + s.Color.R.ToString("X2") + s.Color.G.ToString("X2") + s.Color.B.ToString("X2");
            }
        }

        public static TextBlock Text(string text, double size, Brush brush)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = size,
                FontFamily = Font,
                Foreground = brush ?? Ink,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
        }

        /// <summary>Selene-style focus: accent outline plus a slight scale-up.</summary>
        public static void SetFocused(Border b, bool focused)
        {
            b.BorderBrush = focused ? Accent : Brushes.Transparent;
            b.RenderTransformOrigin = new Point(0.5, 0.5);
            b.RenderTransform = focused ? new ScaleTransform(1.06, 1.06) : Transform.Identity;
            System.Windows.Controls.Panel.SetZIndex(b, focused ? 1 : 0);
        }

        /// <summary>A focusable-looking tile (border + centered label) used by the bar, menu and keyboard.</summary>
        public static Border Tile(string label, double size)
        {
            var b = new Border
            {
                Background = Key,
                BorderThickness = new Thickness(3),
                BorderBrush = Brushes.Transparent,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(4)
            };
            var t = Text(label, size, Ink);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            b.Child = t;
            return b;
        }
    }
}
