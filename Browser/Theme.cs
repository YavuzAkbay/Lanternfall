// Theme.cs - the Lanternfall (Selene) look for the Browser and YouTube screens: palette brushes, fonts, Material icons,
// focus visuals (a filled pill, a key, or a card ring with the theme's glow), glass panels and button-badge hints.
// SeleneBrowser.dll cannot reference SeleneCore, so everything is read at runtime from the application resources
// ("SeleneInkColor", "SeleneFocusColor", ...) with hard-coded fallbacks, and mirrors SeleneCore's Ui (Pill, Tile, Prompt).
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace SeleneBrowser
{
    /// <summary>How a focusable Border shows focus. Pill/Ghost/Key fill with the focus color; Card grows a glowing ring.</summary>
    internal enum Look { Pill, Ghost, Key, Card }

    internal static class Theme
    {
        public static readonly FontFamily Font = new FontFamily("Segoe UI");
        public static readonly FontFamily Display = new FontFamily("Segoe UI Variable Display, Segoe UI");

        // ------------------------------------------------------------------ palette

        // ---- live palette -------------------------------------------------------------------------------------------
        // Every palette brush handed out here is ONE shared, unfrozen SolidColorBrush per (color key, opacity). When the user
        // picks another Lanternfall theme, Sync() re-colours those brushes in place, so every element that already holds one
        // (a whole built screen) changes colour at once, with no rebuild. Off the UI thread a frozen copy is returned instead.

        private sealed class LiveSpec { public string Key, Fallback; public double Alpha; }
        private static readonly Dictionary<string, SolidColorBrush> liveBrushes = new Dictionary<string, SolidColorBrush>();
        private static readonly Dictionary<string, LiveSpec> liveSpecs = new Dictionary<string, LiveSpec>();
        private static string signature;

        /// <summary>Raised (on the UI thread) after the palette changed and the shared brushes were re-coloured.</summary>
        public static event Action Changed;

        private static bool OnUiThread()
        {
            var app = Application.Current;
            return app != null && app.Dispatcher.CheckAccess();
        }

        private static Color Resolve(string key, string fallbackHex, double alpha)
        {
            Color c = ColorOf(key, fallbackHex);
            return Color.FromArgb((byte)Math.Round(c.A * Math.Max(0, Math.Min(1, alpha))), c.R, c.G, c.B);
        }

        /// <summary>Palette brush by resource key, or a solid brush of the fallback color.</summary>
        public static Brush Get(string key, string fallbackHex)
        {
            // the color-backed keys follow the theme live; anything else (the gradient backdrop) is the resource itself
            string colorKey = null;
            switch (key)
            {
                case "SeleneInkBrush": colorKey = "SeleneInkColor"; break;
                case "SeleneFocusBrush": colorKey = "SeleneFocusColor"; break;
                case "SeleneOnFocusBrush": colorKey = "SeleneOnFocusColor"; break;
                case "SeleneScrimBrush": colorKey = "SeleneScrimColor"; break;
                case "SeleneGlassBrush": colorKey = "SeleneGlassColor"; break;
            }
            if (colorKey != null) return Tint(colorKey, fallbackHex, 1);
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

        /// <summary>The theme's wallpaper as an image (null when the theme has none).</summary>
        public static ImageSource Wallpaper
        {
            get
            {
                try { var app = Application.Current; return app != null ? app.TryFindResource("SeleneWallpaper") as ImageSource : null; }
                catch { return null; }
            }
        }

        /// <summary>The picked theme's id ("selene", "nord", ...), "" when unknown.</summary>
        public static string ThemeId
        {
            get
            {
                try { var app = Application.Current; return (app != null ? app.TryFindResource("SeleneThemeId") as string : null) ?? ""; }
                catch { return ""; }
            }
        }

        /// <summary>True for a light palette (its scrim is light, ink is dark).</summary>
        public static bool IsLight
        {
            get
            {
                Color ink = InkColor;
                return (ink.R * 0.299 + ink.G * 0.587 + ink.B * 0.114) < 128;
            }
        }

        /// <summary>Notices a theme change and re-colours every shared brush, glow and focus tile. Cheap; call it whenever a
        /// screen is shown (and from a timer while one stays up).</summary>
        public static bool Sync()
        {
            if (!OnUiThread()) return false;
            string sig = ThemeId + "|" + InkColor + FocusColor + OnFocusColor + ScrimColor + GlassColor + ColorOf("GlyphColor", "#FF9CCBFF");
            if (sig == signature) return false;
            bool first = signature == null;
            signature = sig;
            foreach (KeyValuePair<string, SolidColorBrush> kv in liveBrushes)
            {
                LiveSpec sp = liveSpecs[kv.Key];
                kv.Value.Color = Resolve(sp.Key, sp.Fallback, sp.Alpha);
            }
            cardGlow = null;
            haloGlow = null;
            focusRing = null;
            RepaintAll();
            if (!first && Changed != null)
            {
                foreach (Delegate d in Changed.GetInvocationList())
                {
                    try { ((Action)d)(); } catch { }   // one broken screen must not keep the others from re-colouring
                }
            }
            return !first;
        }

        /// <summary>Palette color by resource key ("SeleneInkColor", ...), or the fallback.</summary>
        public static Color ColorOf(string key, string fallbackHex)
        {
            try
            {
                var app = Application.Current;
                if (app != null)
                {
                    object o = app.TryFindResource(key);
                    if (o is Color) return (Color)o;
                }
            }
            catch { }
            return (Color)ColorConverter.ConvertFromString(fallbackHex);
        }

        /// <summary>A palette color at an opacity, as a frozen brush (Selene's Ink.Brush(key, alpha)).</summary>
        public static Brush Tint(string key, string fallbackHex, double alpha)
        {
            if (!OnUiThread())
            {
                var f = new SolidColorBrush(Resolve(key, fallbackHex, alpha));
                f.Freeze();
                return f;
            }
            if (signature == null) Sync();
            string id = key + "|" + Math.Round(alpha, 3).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            SolidColorBrush b;
            if (!liveBrushes.TryGetValue(id, out b))
            {
                b = new SolidColorBrush(Resolve(key, fallbackHex, alpha));
                liveBrushes[id] = b;
                liveSpecs[id] = new LiveSpec { Key = key, Fallback = fallbackHex, Alpha = alpha };
            }
            return b;
        }

        /// <summary>The theme's backdrop (SeleneBaseBrush gradient) bound to a panel's Background, so it follows a theme change.</summary>
        public static void BindBackdrop(System.Windows.Controls.Panel p)
        {
            p.SetResourceReference(System.Windows.Controls.Panel.BackgroundProperty, "SeleneBaseBrush");
        }

        public static Color InkColor { get { return ColorOf("SeleneInkColor", "#FFFFFFFF"); } }
        public static Color FocusColor { get { return ColorOf("SeleneFocusColor", "#FFFFFFFF"); } }
        public static Color OnFocusColor { get { return ColorOf("SeleneOnFocusColor", "#FF111111"); } }
        public static Color ScrimColor { get { return ColorOf("SeleneScrimColor", "#FF000000"); } }
        public static Color GlassColor { get { return ColorOf("SeleneGlassColor", "#FF14181F"); } }

        public static Brush Ink { get { return Get("SeleneInkBrush", "#FFFFFFFF"); } }
        public static Brush Ink90 { get { return InkA(0.9); } }
        public static Brush Ink60 { get { return InkA(0.6); } }
        public static Brush Accent { get { return Get("SeleneFocusBrush", "#FFFFFFFF"); } }
        public static Brush OnAccent { get { return Get("SeleneOnFocusBrush", "#FF111111"); } }
        public static Brush Background { get { return Get("SeleneBaseBrush", "#FF0B0E14"); } }
        public static Brush Panel { get { return GlassA(0.94); } }
        public static Brush Key { get { return InkA(0.1); } }

        // Liquid glass (approved 2026-11-27; the theme's Constants.xaml and SeleneCore's Liquid have the same values): every
        // button is the opaque ButtonFill with the Rim, every container gets at least the Rim. The rim is a thin line of plain
        // white that catches the light on the curved ends (top-right, bottom-left) and all but vanishes along straight edges.
        public const double RimWidth = 1.25;
        public static Thickness RimThickness { get { return new Thickness(RimWidth); } }
        private static Brush rim;
        public static Brush Rim
        {
            get
            {
                if (rim != null) return rim;
                var g = new LinearGradientBrush { StartPoint = new Point(1, 0), EndPoint = new Point(0, 1) };
                g.GradientStops.Add(new GradientStop(Color.FromArgb(0x99, 255, 255, 255), 0));
                g.GradientStops.Add(new GradientStop(Color.FromArgb(0x66, 255, 255, 255), 0.07));
                g.GradientStops.Add(new GradientStop(Color.FromArgb(0x1A, 255, 255, 255), 0.2));
                g.GradientStops.Add(new GradientStop(Color.FromArgb(0x0C, 255, 255, 255), 0.5));
                g.GradientStops.Add(new GradientStop(Color.FromArgb(0x1A, 255, 255, 255), 0.8));
                g.GradientStops.Add(new GradientStop(Color.FromArgb(0x66, 255, 255, 255), 0.93));
                g.GradientStops.Add(new GradientStop(Color.FromArgb(0x8C, 255, 255, 255), 1));
                g.Freeze();
                rim = g;
                return rim;
            }
        }
        private static Brush focusRim, focusRing;
        /// <summary>The rim of a focused button (a light solid focus fill): bright ends, a faint dark line between them.</summary>
        public static Brush FocusRim
        {
            get
            {
                if (focusRim != null) return focusRim;
                var g = new LinearGradientBrush { StartPoint = new Point(1, 0), EndPoint = new Point(0, 1) };
                g.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 255, 255, 255), 0));
                g.GradientStops.Add(new GradientStop(Color.FromArgb(0xCC, 255, 255, 255), 0.07));
                g.GradientStops.Add(new GradientStop(Color.FromArgb(0x26, 0, 0, 0), 0.2));
                g.GradientStops.Add(new GradientStop(Color.FromArgb(0x33, 0, 0, 0), 0.5));
                g.GradientStops.Add(new GradientStop(Color.FromArgb(0x26, 0, 0, 0), 0.8));
                g.GradientStops.Add(new GradientStop(Color.FromArgb(0xCC, 255, 255, 255), 0.93));
                g.GradientStops.Add(new GradientStop(Color.FromArgb(0xF0, 255, 255, 255), 1));
                g.Freeze();
                focusRim = g;
                return focusRim;
            }
        }
        /// <summary>The focus ring around a focused card: the focus color lit toward white on the curves (rebuilt on a theme change).</summary>
        public static Brush FocusRing
        {
            get
            {
                if (focusRing != null) return focusRing;
                Color c = FocusColor;
                var g = new LinearGradientBrush { StartPoint = new Point(1, 0), EndPoint = new Point(0, 1) };
                double[,] stops = { { 1, 0.55, 0 }, { 1, 0, 0.1 }, { 0.6, 0, 0.3 }, { 0.45, 0, 0.5 }, { 0.6, 0, 0.7 }, { 1, 0, 0.9 }, { 1, 0.45, 1 } };
                for (int i = 0; i < stops.GetLength(0); i++)
                {
                    double a = stops[i, 0], w = stops[i, 1];
                    g.GradientStops.Add(new GradientStop(Color.FromArgb((byte)Math.Round(a * 255), (byte)Math.Round(c.R + (255 - c.R) * w), (byte)Math.Round(c.G + (255 - c.G) * w), (byte)Math.Round(c.B + (255 - c.B) * w)), stops[i, 2]));
                }
                g.Freeze();
                focusRing = g;
                return focusRing;
            }
        }
        /// <summary>The opaque button fill (the palette's SeleneButtonColor: glass lifted by a little white).</summary>
        public static Brush ButtonFill { get { return Tint("SeleneButtonColor", "#FF282C32", 1); } }
        /// <summary>The rim on any border (a container).</summary>
        public static void Edge(Border b) { b.BorderBrush = Rim; b.BorderThickness = RimThickness; }

        public static Brush InkA(double a) { return Tint("SeleneInkColor", "#FFFFFFFF", a); }
        public static Brush GlassA(double a) { return Tint("SeleneGlassColor", "#FF14181F", a); }
        public static Brush ScrimA(double a) { return Tint("SeleneScrimColor", "#FF000000", a); }

        /// <summary>Accent color as #RRGGBB (for injected page CSS).</summary>
        public static string AccentHex { get { return Hex(FocusColor); } }
        public static string Hex(Color c) { return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"); }
        /// <summary>A color as CSS rgba(), alpha 0..1 times the color's own.</summary>
        public static string Css(Color c, double alpha)
        {
            return "rgba(" + c.R + "," + c.G + "," + c.B + "," + (c.A / 255.0 * alpha).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + ")";
        }

        /// <summary>The theme's backdrop (SeleneBaseBrush) as a CSS background, for pages drawn in the theme's colors.</summary>
        public static string BackdropCss()
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            Brush b = Background;
            var solid = b as SolidColorBrush;
            if (solid != null) return Css(solid.Color, 1);
            var grad = b as GradientBrush;
            if (grad == null || grad.GradientStops.Count == 0) return Css(ScrimColor, 1);
            var stops = new List<string>();
            foreach (GradientStop st in grad.GradientStops)
                stops.Add(Css(st.Color, grad.Opacity) + " " + (st.Offset * 100).ToString("0.#", inv) + "%");
            string list = string.Join(",", stops.ToArray());
            Color baseColor = grad.GradientStops[grad.GradientStops.Count - 1].Color;
            var radial = grad as RadialGradientBrush;
            if (radial != null)
            {
                return Css(baseColor, 1) + " radial-gradient(ellipse " + (radial.RadiusX * 100).ToString("0", inv) + "% " + (radial.RadiusY * 100).ToString("0", inv) + "% at "
                    + (radial.Center.X * 100).ToString("0", inv) + "% " + (radial.Center.Y * 100).ToString("0", inv) + "%," + list + ") no-repeat fixed";
            }
            var linear = grad as LinearGradientBrush;
            double deg = 180;
            if (linear != null)
            {
                Vector v = linear.EndPoint - linear.StartPoint;
                deg = Math.Atan2(v.X, -v.Y) * 180 / Math.PI;   // CSS: 0deg points up, clockwise
            }
            return Css(baseColor, 1) + " linear-gradient(" + deg.ToString("0", inv) + "deg," + list + ") no-repeat fixed";
        }

        // ------------------------------------------------------------------ text and icons

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

        /// <summary>A screen title: Selene's display face, semibold.</summary>
        public static TextBlock Title(string text, double size)
        {
            TextBlock t = Text(text, size, Ink);
            t.FontFamily = Display;
            t.FontWeight = FontWeights.SemiBold;
            return t;
        }

        // Material Icons, shipped by the theme (theme\Fonts\MaterialIcons-Regular.ttf); null when it can't be found
        private static bool iconsLooked;
        private static GlyphTypeface iconFace;
        private static FontFamily iconFamily;

        private static void FindIcons()
        {
            if (iconsLooked) return;
            iconsLooked = true;
            try
            {
                var roots = new List<string>();
                roots.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Themes", "Fullscreen"));
                roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Playnite", "Themes", "Fullscreen"));
                foreach (string root in roots)
                {
                    if (!Directory.Exists(root)) continue;
                    // Lanternfall's own folder first (its id carries the name), then any theme that ships the font
                    var dirs = new List<string>(Directory.GetDirectories(root));
                    dirs.Sort(delegate(string a, string b)
                    {
                        bool la = a.IndexOf("Lanternfall", StringComparison.OrdinalIgnoreCase) >= 0 || a.IndexOf("Selene", StringComparison.OrdinalIgnoreCase) >= 0;
                        bool lb = b.IndexOf("Lanternfall", StringComparison.OrdinalIgnoreCase) >= 0 || b.IndexOf("Selene", StringComparison.OrdinalIgnoreCase) >= 0;
                        return la == lb ? string.Compare(a, b, StringComparison.OrdinalIgnoreCase) : (la ? -1 : 1);
                    });
                    foreach (string dir in dirs)
                    {
                        string fonts = Path.Combine(dir, "Fonts");
                        string file = Path.Combine(fonts, "MaterialIcons-Regular.ttf");
                        if (!File.Exists(file)) continue;
                        iconFace = new GlyphTypeface(new Uri(file));
                        iconFamily = new FontFamily(new Uri(fonts.TrimEnd('\\') + "\\"), "./#Material Icons");
                        return;
                    }
                }
            }
            catch { iconFace = null; iconFamily = null; }
        }

        /// <summary>True when the theme's icon font is there and has this code point.</summary>
        public static bool HasIcon(int code)
        {
            FindIcons();
            try { return iconFace != null && iconFace.CharacterToGlyphMap.ContainsKey(code); }
            catch { return false; }
        }

        /// <summary>A Material icon centred by its ink (like SeleneCore's Ui.Glyph), or null without the font.</summary>
        public static FrameworkElement Icon(int code, double size, Brush fill)
        {
            if (!HasIcon(code)) return null;
            try
            {
                ushort glyph = iconFace.CharacterToGlyphMap[code];
                Geometry geo = iconFace.GetGlyphOutline(glyph, size, size);
                Rect b = geo.Bounds;
                if (b.IsEmpty || b.Width < 1 || b.Height < 1) return null;
                geo = geo.Clone();
                geo.Transform = new TranslateTransform(-b.X, -b.Y);
                geo.Freeze();
                return new System.Windows.Shapes.Path
                {
                    Data = geo, Fill = fill, Width = b.Width, Height = b.Height, Stretch = Stretch.None,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
                };
            }
            catch { return null; }
        }

        private static void Paint(FrameworkElement el, Brush b)
        {
            var shape = el as System.Windows.Shapes.Shape;
            if (shape != null) { shape.Fill = b; return; }
            var tb = el as TextBlock;
            if (tb != null) tb.Foreground = b;
        }

        // ------------------------------------------------------------------ focusable tiles

        private sealed class State
        {
            public Look Kind;
            public bool Focused, Selected, Toggled;
            public TextBlock Label;
            public FrameworkElement Icon;
            public FrameworkElement IconSlot;
            public StackPanel Content;
            public Border Halo;      // Pill/Ghost/Key: a twin of the face carrying the glow, so the words stay sharp
            public Border Ring;      // Card: the focus ring around the face
            public double Scale = 1.06;
        }

        private static readonly ConditionalWeakTable<Border, State> states = new ConditionalWeakTable<Border, State>();

        private static State StateOf(Border b)
        {
            State s;
            return b != null && states.TryGetValue(b, out s) ? s : null;
        }

        // every tile/card made, weakly, so a theme change can repaint them (glows and the focused fill are cached)
        private static readonly List<WeakReference> tiles = new List<WeakReference>();

        private static void Register(Border b, State s)
        {
            states.Add(b, s);
            if (tiles.Count > 256) tiles.RemoveAll(delegate(WeakReference w) { return !w.IsAlive; });
            tiles.Add(new WeakReference(b));
        }

        private static void RepaintAll()
        {
            foreach (WeakReference w in tiles.ToArray())
            {
                var b = w.Target as Border;
                State s = StateOf(b);
                if (s != null) Repaint(b, s);
            }
        }

        /// <summary>A Selene pill (glass at rest, filled with the focus color when focused) with a label.</summary>
        public static Border Tile(string label, double size) { return Tile(label, size, 0, Look.Pill); }

        /// <summary>A focusable tile: optional Material icon (code 0 = none) before the label (null/"" = icon only).</summary>
        public static Border Tile(string label, double size, int icon, Look look)
        {
            var b = new Border
            {
                BorderThickness = RimThickness,
                CornerRadius = new CornerRadius(look == Look.Key ? 14 : 20),
                Padding = new Thickness(22, 8, 22, 9),
                Margin = new Thickness(6),
                UseLayoutRounding = true
            };
            // a pill rounds to half its height (WPF draws an oversized radius as an ellipse, not a capsule)
            if (look != Look.Key)
            {
                Border pill = b;
                b.SizeChanged += delegate
                {
                    double r = Math.Max(0, Math.Min(pill.ActualHeight, pill.ActualWidth) / 2);
                    pill.CornerRadius = new CornerRadius(r);
                    State st = StateOf(pill);
                    if (st != null && st.Halo != null) st.Halo.CornerRadius = pill.CornerRadius;
                };
            }
            // Ideal (WPF default): Display snaps glyphs to pixels before the Viewbox scales, so text looks wrong on scaled overlays.
            TextOptions.SetTextFormattingMode(b, TextFormattingMode.Ideal);
            var s = new State { Kind = look, Scale = look == Look.Key ? 1.08 : 1.05 };
            var host = new Grid();
            s.Halo = new Border { CornerRadius = b.CornerRadius, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
            host.Children.Add(s.Halo);
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            s.Content = row;
            if (icon != 0)
            {
                s.Icon = Icon(icon, size * 1.15, Ink);
                if (s.Icon != null)
                {
                    // a fixed slot, so labels after different icons line up (the rail, menus)
                    var slot = new Grid { Width = Math.Round(size * 1.2), VerticalAlignment = VerticalAlignment.Center };
                    slot.Margin = new Thickness(0, 0, string.IsNullOrEmpty(label) ? 0 : size * 0.55, 0);
                    slot.Children.Add(s.Icon);
                    s.IconSlot = slot;
                    row.Children.Add(slot);
                }
            }
            if (!string.IsNullOrEmpty(label) || s.Icon == null)
            {
                s.Label = Text(label ?? "", size, Ink);
                s.Label.FontFamily = look == Look.Key ? Font : Display;
                s.Label.HorizontalAlignment = HorizontalAlignment.Center;
                row.Children.Add(s.Label);
            }
            host.Children.Add(row);
            b.Child = host;
            Register(b, s);
            Repaint(b, s);
            return b;
        }

        /// <summary>The tile's label (null for an icon-only tile).</summary>
        public static TextBlock LabelOf(Border b) { State s = StateOf(b); return s != null ? s.Label : null; }

        public static void SetLabel(Border b, string text)
        {
            State s = StateOf(b);
            if (s != null && s.Label != null) s.Label.Text = text;
        }

        /// <summary>How much the tile grows when focused (a row in a clipped list grows less).</summary>
        public static void SetFocusScale(Border b, double scale)
        {
            State s = StateOf(b);
            if (s != null) s.Scale = scale;
        }

        /// <summary>Shows or hides the tile's icon, keeping its room (a list's check marks).</summary>
        public static void SetIconShown(Border b, bool on)
        {
            State s = StateOf(b);
            if (s != null && s.Icon != null) s.Icon.Opacity = on ? 1 : 0;
        }

        /// <summary>Lines the tile's content up on the left (menu rows, the rail).</summary>
        public static void AlignLeft(Border b)
        {
            State s = StateOf(b);
            if (s == null) return;
            s.Content.HorizontalAlignment = HorizontalAlignment.Left;
            if (s.Label != null) s.Label.HorizontalAlignment = HorizontalAlignment.Left;
        }

        /// <summary>Puts a caller-made TextBlock in place of the tile's label (the address bar's text).</summary>
        public static void AdoptLabel(Border b, TextBlock label)
        {
            State s = StateOf(b);
            if (s == null) return;
            if (s.Label != null) s.Content.Children.Remove(s.Label);
            s.Label = label;
            s.Content.Children.Add(label);
            if (s.IconSlot != null) s.IconSlot.Margin = new Thickness(0, 0, label.FontSize * 0.55, 0);
            Repaint(b, s);
        }

        /// <summary>The chosen one of a group (the open tab): a soft fill and the accent on its icon.</summary>
        public static void SetSelected(Border b, bool on)
        {
            State s = StateOf(b);
            if (s == null || s.Selected == on) return;
            s.Selected = on;
            Repaint(b, s);
        }

        /// <summary>A switch that is on (Shift): a stronger fill than at rest.</summary>
        public static void SetToggled(Border b, bool on)
        {
            State s = StateOf(b);
            if (s == null || s.Toggled == on) return;
            s.Toggled = on;
            Repaint(b, s);
        }

        /// <summary>Selene-style focus: a filled pill / key, or a card's glowing ring, with a short scale-up.</summary>
        public static void SetFocused(Border b, bool focused)
        {
            if (b == null) return;
            State s = StateOf(b);
            if (s == null)
            {
                // a plain border: accent outline (nothing should get here any more, kept as a safety net)
                b.BorderBrush = focused ? FocusRing : Rim;
                ScaleTo(b, focused ? 1.04 : 1);
                System.Windows.Controls.Panel.SetZIndex(b, focused ? 1 : 0);
                return;
            }
            if (s.Focused == focused) return;
            s.Focused = focused;
            Repaint(b, s);
            ScaleTo(b, focused ? s.Scale : 1);
            System.Windows.Controls.Panel.SetZIndex(b, focused ? 1 : 0);
        }

        // Focus changes can arrive every ~90 ms while scrolling a grid, so the glows are built once and
        // shared instead of per focus. They are built LAZILY, on the first focus change, because a static
        // constructor could run before Playnite has merged the theme palette into Application.Resources
        // and would then freeze the wrong (fallback) colour for the whole session.
        private static DropShadowEffect cardGlow;
        private static DropShadowEffect haloGlow;

        private static void EnsureGlows()
        {
            if (cardGlow != null && haloGlow != null) return;
            Color focus = FocusColor;
            cardGlow = MakeGlow(18, 0.55, focus);
            haloGlow = MakeGlow(22, 0.42, focus);
        }

        private static DropShadowEffect MakeGlow(double blur, double opacity, Color color)
        {
            var g = new DropShadowEffect
            {
                ShadowDepth = 0, BlurRadius = blur, Opacity = opacity, Color = color, RenderingBias = RenderingBias.Performance
            };
            g.Freeze();
            return g;
        }

        private static void Repaint(Border b, State s)
        {
            EnsureGlows();
            if (s.Kind == Look.Card)
            {
                if (s.Focused) s.Ring.BorderBrush = FocusRing;   // (a theme change rebuilds the ring's colors)
                s.Ring.Opacity = s.Focused ? 1 : 0;
                s.Ring.Effect = s.Focused ? cardGlow : null;
                return;
            }
            Brush fill, edge, ink, iconInk;
            if (s.Focused)
            {
                fill = Accent; edge = FocusRim; ink = OnAccent; iconInk = OnAccent;
            }
            else if (s.Toggled)
            {
                fill = InkA(0.32); edge = Rim; ink = Ink; iconInk = Ink;
            }
            else if (s.Selected)
            {
                fill = InkA(0.14); edge = Rim; ink = Ink; iconInk = Accent;
            }
            else if (s.Kind == Look.Ghost)
            {
                fill = Brushes.Transparent; edge = Brushes.Transparent; ink = Ink90; iconInk = InkA(0.75);
            }
            else
            {
                fill = ButtonFill; edge = Rim; ink = Ink; iconInk = Ink;   // liquid glass button
            }
            b.Background = fill;
            b.BorderBrush = edge;
            if (s.Label != null)
            {
                s.Label.Foreground = ink;
                s.Label.FontWeight = s.Focused || s.Selected ? FontWeights.SemiBold : FontWeights.Normal;
            }
            if (s.Icon != null) Paint(s.Icon, iconInk);
            // the glow: a twin of the face behind the content, stretched over the padding, only while focused
            s.Halo.Visibility = s.Focused ? Visibility.Visible : Visibility.Collapsed;
            s.Halo.Effect = s.Focused ? haloGlow : null;
            s.Halo.Background = s.Focused ? Accent : null;
            Thickness p = b.Padding, t = b.BorderThickness;
            s.Halo.Margin = new Thickness(-(p.Left + t.Left), -(p.Top + t.Top), -(p.Right + t.Right), -(p.Bottom + t.Bottom));
            s.Halo.CornerRadius = b.CornerRadius;
        }

        private static void ScaleTo(FrameworkElement el, double to)
        {
            var st = el.RenderTransform as ScaleTransform;
            // A repeated focus nudge at the same scale would restart the animation and re-rasterize
            // an already-blurred (glowing) element; skip it when an unfrozen transform is already there.
            if (st != null && !st.IsFrozen &&
                Math.Abs(st.ScaleX - to) < 0.01 && Math.Abs(st.ScaleY - to) < 0.01) return;
            if (st == null || st.IsFrozen)
            {
                st = new ScaleTransform(1, 1);
                el.RenderTransform = st;
                el.RenderTransformOrigin = new Point(0.5, 0.5);
            }
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var d = TimeSpan.FromMilliseconds(to > 1 ? 180 : 150);
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(to, d) { EasingFunction = ease });
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(to, d) { EasingFunction = ease });
        }

        /// <summary>A card like Selene's game cards: a glass face with rounded corners, and around it (6 px out) the
        /// 3 px focus ring that glows while focused. Put the content in the returned face Grid.</summary>
        public static Border Card(double width, double height, double radius, out Grid face)
        {
            const double gap = 6;
            var outer = new Border { UseLayoutRounding = true };
            if (!double.IsNaN(width)) outer.Width = width;
            if (!double.IsNaN(height)) outer.Height = height;
            // Ideal (WPF default): Display snaps glyphs to pixels before the Viewbox scales, so text looks wrong on scaled overlays.
            TextOptions.SetTextFormattingMode(outer, TextFormattingMode.Ideal);
            var s = new State { Kind = Look.Card, Scale = 1.05 };
            var host = new Grid();
            s.Ring = new Border
            {
                CornerRadius = new CornerRadius(radius + gap), BorderThickness = new Thickness(3),
                BorderBrush = FocusRing, Opacity = 0, IsHitTestVisible = false
            };
            host.Children.Add(s.Ring);
            var faceBorder = new Border { CornerRadius = new CornerRadius(radius), Background = GlassA(0.72), Margin = new Thickness(gap) };
            face = new Grid();
            // rounded clip that follows the face's size (the thumbnail's corners)
            var clip = new RectangleGeometry { RadiusX = radius, RadiusY = radius };
            Grid f = face;
            face.SizeChanged += delegate { clip.Rect = new Rect(0, 0, f.ActualWidth, f.ActualHeight); };
            face.Clip = clip;
            faceBorder.Child = face;
            host.Children.Add(faceBorder);
            // the hairline edge over the content (a border on the face would push the content in)
            host.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(radius), BorderThickness = RimThickness, BorderBrush = Rim,
                Margin = new Thickness(gap), IsHitTestVisible = false
            });
            outer.Child = host;
            Register(outer, s);
            return outer;
        }

        /// <summary>A glass panel (menus, the keyboard, the Up next list): Selene's dialog card.</summary>
        public static void Glass(Border b, double radius)
        {
            b.Background = GlassA(0.95);
            Edge(b);
            b.CornerRadius = new CornerRadius(radius);
            b.UseLayoutRounding = true;
            // Ideal (WPF default): Display snaps glyphs to pixels before the Viewbox scales, so text looks wrong on scaled overlays.
            TextOptions.SetTextFormattingMode(b, TextFormattingMode.Ideal);
        }

        /// <summary>A small label on a thumbnail (duration, LIVE, NEW).</summary>
        public static Border Chip(string text, Brush back, Brush fore, double size)
        {
            TextBlock t = Text(text, size, fore);
            t.FontWeight = FontWeights.SemiBold;
            return new Border { Background = back, CornerRadius = new CornerRadius(7), Padding = new Thickness(9, 2, 9, 3), Child = t, IsHitTestVisible = false };
        }

        // ------------------------------------------------------------------ button badges

        /// <summary>The label a hint's button shows: the theme's own (Selene publishes "SelenePromptA" ... for Xbox /
        /// PlayStation / keyboard), else the name.</summary>
        public static string PromptLabel(string button)
        {
            string key = button;
            switch (button)
            {
                case "R-stick": case "RStick": key = "RS"; break;
                case "Back": key = "View"; break;
            }
            try
            {
                var app = Application.Current;
                var s = app != null ? app.TryFindResource("SelenePrompt" + key) as string : null;
                if (!string.IsNullOrEmpty(s)) return s;
            }
            catch { }
            if (button == "R-stick") return "RS";
            if (button == "L-stick") return "LS";
            return button;
        }

        // a direction or the d-pad: an arrow icon in the badge instead of a word
        private static int BadgeIcon(string button)
        {
            switch (button)
            {
                case "Up": return 0xE316;
                case "Down": return 0xE313;
                case "Left": return 0xE314;
                case "Right": return 0xE315;
                case "D-pad": return 0xE30F;   // gamepad
            }
            return 0;
        }

        /// <summary>A round button badge like Selene's prompts: ink disc, the letter in the scrim color.</summary>
        public static FrameworkElement Badge(string button, double height)
        {
            var g = new Grid { Height = height, VerticalAlignment = VerticalAlignment.Center };
            g.Children.Add(new Border { CornerRadius = new CornerRadius(height / 2), Background = InkA(0.92) });
            Brush letterInk = Tint("SeleneScrimColor", "#FF000000", 1);
            int icon = BadgeIcon(button);
            FrameworkElement mark = icon != 0 ? Icon(icon, height * 0.78, letterInk) : null;
            if (mark != null)
            {
                g.Width = height;
                g.Children.Add(mark);
                return g;
            }
            string shown = PromptLabel(button);
            g.Width = Math.Max(height, shown.Length > 1 ? shown.Length * height * 0.27 + height * 0.5 : height);
            var t = new TextBlock
            {
                Text = shown, FontFamily = Font, FontWeight = FontWeights.SemiBold, FontSize = shown.Length > 1 ? height * 0.4 : height * 0.52,
                Foreground = letterInk, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, height * 0.05)
            };
            g.Children.Add(t);
            return g;
        }
    }

    /// <summary>A row of button hints drawn like Selene's: round badges and what they do. Takes the screens' plain hint
    /// strings ("D-pad Move    A Select    LB/RB Tabs    Y / B Back") so the Hints contract stays a string.</summary>
    internal sealed class HintBar : WrapPanel
    {
        private string shown;
        private readonly double size;

        public HintBar(double size, HorizontalAlignment align)
        {
            this.size = size;
            HorizontalAlignment = align;
            VerticalAlignment = VerticalAlignment.Center;
            Orientation = Orientation.Horizontal;
            IsHitTestVisible = false;
            UseLayoutRounding = true;
            // Ideal (WPF default): Display snaps glyphs to pixels before the Viewbox scales, so text looks wrong on scaled overlays.
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        }

        public string Text { get { return shown ?? ""; } set { Show(value); } }

        public void Show(string hints)
        {
            if (hints == shown) return;
            shown = hints;
            Children.Clear();
            if (string.IsNullOrEmpty(hints)) return;
            double badge = Math.Round(size * 1.45);
            foreach (string raw in hints.Split(new[] { "    " }, StringSplitOptions.RemoveEmptyEntries))
            {
                string seg = raw.Trim();
                if (seg.Length == 0) continue;
                string[] tok = seg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var buttons = new List<string> { tok[0] };
                int i = 1;
                while (i + 1 < tok.Length && tok[i] == "/") { buttons.Add(tok[i + 1]); i += 2; }
                string label = string.Join(" ", tok, i, tok.Length - i);
                var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(size * 1.1, 3, 0, 3) };
                bool first = true;
                foreach (string b in buttons)
                    foreach (string part in b.Split('/'))
                    {
                        if (part.Length == 0) continue;
                        FrameworkElement el = Theme.Badge(part, badge);
                        if (!first) el.Margin = new Thickness(4, 0, 0, 0);
                        first = false;
                        item.Children.Add(el);
                    }
                if (label.Length > 0)
                {
                    TextBlock t = Theme.Text(label, size, Theme.Ink90);
                    t.Margin = new Thickness(size * 0.5, 0, 0, 1);
                    item.Children.Add(t);
                }
                Children.Add(item);
            }
        }
    }
}
