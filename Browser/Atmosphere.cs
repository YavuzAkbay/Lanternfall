// Atmosphere.cs - the backdrop behind the Browser and YouTube screens, built like Lanternfall's home screen
// (theme\Views\Main.xaml, SeleneHubGameBackground): the theme's own backdrop gradient, its wallpaper, the accent beam / wash /
// haze where the theme has no wallpaper, an optional "ambient art" layer (the focused video's thumbnail melted into colour,
// like the game art on the home screen) and the three scrims (left, bottom, top) that keep text readable in dark and light
// themes. Everything follows the picked theme live (Theme.Sync / Theme.Changed).
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace SeleneBrowser
{
    internal sealed class Atmosphere : Grid
    {
        private readonly Image wallpaper = new Image { Stretch = Stretch.UniformToFill, IsHitTestVisible = false };
        private readonly Rectangle beam = new Rectangle { IsHitTestVisible = false };
        private readonly Rectangle wash = new Rectangle { IsHitTestVisible = false };
        private readonly Rectangle haze = new Rectangle { IsHitTestVisible = false };
        private readonly Image artA = new Image { Stretch = Stretch.UniformToFill, Opacity = 0, IsHitTestVisible = false };
        private readonly Image artB = new Image { Stretch = Stretch.UniformToFill, Opacity = 0, IsHitTestVisible = false };
        private readonly Rectangle shadeLeft = new Rectangle { IsHitTestVisible = false };
        private readonly Rectangle shadeBottom = new Rectangle { IsHitTestVisible = false };
        private readonly Rectangle shadeTop = new Rectangle { IsHitTestVisible = false };
        private readonly DispatcherTimer watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        private bool artOnA = true;
        private ImageSource currentArt;
        private const double artStrength = 0.5;   // how strongly the ambient art shows
        private double shade = 0.6;   // Selene's auto shade runs 0.42..0.88; a middle value reads well on any wallpaper

        public Atmosphere()
        {
            IsHitTestVisible = false;
            ClipToBounds = true;
            Theme.BindBackdrop(this);
            wallpaper.SetResourceReference(Image.SourceProperty, "SeleneWallpaper");
            RenderOptions.SetBitmapScalingMode(wallpaper, BitmapScalingMode.Fant);
            RenderOptions.SetBitmapScalingMode(artA, BitmapScalingMode.Linear);
            RenderOptions.SetBitmapScalingMode(artB, BitmapScalingMode.Linear);
            Children.Add(wallpaper);
            Children.Add(haze);
            Children.Add(wash);
            Children.Add(beam);
            Children.Add(artA);
            Children.Add(artB);
            Children.Add(shadeLeft);
            Children.Add(shadeBottom);
            Children.Add(shadeTop);
            Paint();
            watch.Tick += delegate { Theme.Sync(); UpdateGlow(); };
            // subscribed only while in the visual tree, so a dropped screen is not kept alive by the static event
            Loaded += delegate
            {
                Theme.Changed -= OnThemeChanged;
                Theme.Changed += OnThemeChanged;
                Theme.Sync(); Paint();
                if (IsVisible) watch.Start();
            };
            Unloaded += delegate { watch.Stop(); Theme.Changed -= OnThemeChanged; };
            IsVisibleChanged += delegate
            {
                if (IsVisible) { Theme.Sync(); watch.Start(); } else watch.Stop();
            };
        }

        private void OnThemeChanged() { Paint(); }

        /// <summary>How dark the three scrims are (0..1, default 0.6). A busy page (the player's chrome) may want more.</summary>
        public double Shade
        {
            get { return shade; }
            set { shade = Math.Max(0, Math.Min(1, value)); PaintShades(); }
        }

        /// <summary>Melts an image (a thumbnail, a banner) into the backdrop. null fades it out.</summary>
        public void SetArt(ImageSource art)
        {
            if (ReferenceEquals(art, currentArt)) return;
            currentArt = art;
            FadeTo(art);
        }

        private void FadeTo(ImageSource art)
        {
            Image show = artOnA ? artB : artA;
            Image hide = artOnA ? artA : artB;
            artOnA = !artOnA;
            show.Source = Soften(art);
            double to = art == null ? 0 : artStrength * (Theme.IsLight ? 0.7 : 1);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            show.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease });
            hide.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease });
        }

        // a thumbnail shrunk to a few dozen pixels and scaled back up (linear) becomes soft colour: a free blur
        private static ImageSource Soften(ImageSource art)
        {
            var bmp = art as BitmapSource;
            if (bmp == null || bmp.PixelWidth < 16 || bmp.PixelHeight < 16) return art;
            try
            {
                double k = Math.Min(1.0, 48.0 / bmp.PixelWidth);
                var tb = new TransformedBitmap(bmp, new ScaleTransform(k, k));
                tb.Freeze();
                return tb;
            }
            catch { return art; }
        }

        private static GradientStop Stop(Color c, double alpha, double offset)
        {
            return new GradientStop(Color.FromArgb((byte)Math.Round(255 * Math.Max(0, Math.Min(1, alpha)) * (c.A / 255.0)), c.R, c.G, c.B), offset);
        }

        private static RadialGradientBrush Radial(Point center, double rx, double ry, Color c, double a0, double a1, double a2)
        {
            var g = new RadialGradientBrush { Center = center, GradientOrigin = center, RadiusX = rx, RadiusY = ry };
            g.GradientStops.Add(Stop(c, a0, 0));
            if (a1 >= 0) g.GradientStops.Add(Stop(c, a1, 0.5));
            g.GradientStops.Add(Stop(c, a2, 1));
            g.Freeze();
            return g;
        }

        /// <summary>Re-reads the palette into the gradients (on a theme change and when shown).</summary>
        private void Paint()
        {
            Color glyph = Theme.ColorOf("GlyphColor", "#FF9CCBFF");
            double k = Theme.IsLight ? 0.55 : 1;
            // Lanternfall's beam, wash and haze (LoginScene.BuildGlow) in the theme's accent
            beam.Fill = Radial(new Point(0.14, -0.08), 0.2, 0.85, glyph, 0.42 * k, 0.14 * k, 0);
            wash.Fill = Radial(new Point(0.16, -0.12), 0.6, 1.1, glyph, 0.16 * k, -1, 0);
            haze.Fill = Radial(new Point(0.5, 1.02), 0.95, 0.34, glyph, 0.22 * k, -1, 0);
            PaintShades();
            UpdateGlow();
            if (currentArt != null) FadeTo(currentArt);
        }

        // the home screen's shades: a scrim-coloured veil (dark, or light in a light theme) strongest on the left, the bottom and the top
        private void PaintShades()
        {
            Color s = Theme.ScrimColor;
            double a = shade;
            var left = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            left.GradientStops.Add(Stop(s, 0.88 * a, 0));
            left.GradientStops.Add(Stop(s, 0.50 * a, 0.5));
            left.GradientStops.Add(Stop(s, 0.15 * a, 1));
            left.Freeze();
            shadeLeft.Fill = left;
            var bottom = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            bottom.GradientStops.Add(Stop(s, 0, 0.4));
            bottom.GradientStops.Add(Stop(s, 0.9 * a, 1));
            bottom.Freeze();
            shadeBottom.Fill = bottom;
            var top = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            top.GradientStops.Add(Stop(s, 0.9 * a, 0));
            top.GradientStops.Add(Stop(s, 0.44 * a, 0.1));
            top.GradientStops.Add(Stop(s, 0, 0.26));
            top.Freeze();
            shadeTop.Fill = top;
        }

        // the drawn glow stands in for a wallpaper, exactly as on the home screen; with one it would only muddy the picture
        private void UpdateGlow()
        {
            Visibility v = Theme.Wallpaper != null ? Visibility.Collapsed : Visibility.Visible;
            beam.Visibility = v;
            wash.Visibility = v;
            haze.Visibility = v;
        }
    }
}
