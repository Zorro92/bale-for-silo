using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Hosting;

namespace Bale.Xbox.Services;

/// <summary>
/// The page tint behind the marquee, ported from Silo's Android TV app (AmbientBackdropTint,
/// TvRootHeroBackdrop): the backdrop's average colour with its luminance pulled to 0.22, laid as a
/// diagonal wash, strongest top-right behind the art and fading toward the bottom-left.
/// </summary>
internal static class AmbientTint
{
    private const double TargetLuminance = 0.22;

    private static readonly Dictionary<Uri, Color> Cache = new();

    public static async Task<Color?> SampleAsync(Uri image)
    {
        if (Cache.TryGetValue(image, out var cached))
        {
            return cached;
        }

        try
        {
            // From the artwork cache: the art box shows the same image, so it's downloaded once.
            if (await Artwork.ReadAsync(image) is not { } bytes)
            {
                return null;
            }

            using var stream = bytes.AsBuffer().AsStream().AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(stream);

            // Average a 64x36 downsample, like the Android and tvOS samplers.
            var transform = new BitmapTransform { ScaledWidth = 64, ScaledHeight = 36, InterpolationMode = BitmapInterpolationMode.Linear };
            var pixels = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();

            double r = 0, g = 0, b = 0;
            var count = pixels.Length / 4;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                b += pixels[i];
                g += pixels[i + 1];
                r += pixels[i + 2];
            }

            var tint = Normalize(r / count / 255, g / count / 255, b / count / 255);
            Cache[image] = tint;
            return tint;
        }
        catch (Exception ex)
        {
            Log.Error("Ambient tint", ex);
            return null;
        }
    }

    /// <summary>Scale the colour so its luminance is 0.22: bright art is dimmed, very dark art lifted.</summary>
    internal static Color Normalize(double r, double g, double b)
    {
        var luminance = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        var scale = luminance > 0.001 ? TargetLuminance / luminance : 1;
        byte Channel(double value) => (byte)Math.Round(Math.Clamp(value * scale, 0, 1) * 255);
        return Color.FromArgb(255, Channel(r), Channel(g), Channel(b));
    }

    /// <summary>
    /// TvRootHeroBackdrop.kt: the first sampled tint snaps in; later colours tween for 500 ms using
    /// TvMarqueeEasing. Composition keeps interrupted tweens continuous without touching XAML opacity.
    /// </summary>
    internal sealed class WashAnimation : IDisposable
    {
        private readonly FrameworkElement _host;
        private readonly Compositor _compositor;
        private readonly SpriteVisual _visual;
        private readonly CompositionLinearGradientBrush _brush;
        private readonly CubicBezierEasingFunction _easing;
        private bool _hasTint;

        public WashAnimation(FrameworkElement host)
        {
            _host = host;
            _compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
            _easing = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.42f, 0), new Vector2(0.58f, 1));
            _brush = _compositor.CreateLinearGradientBrush();
            _brush.StartPoint = new Vector2(1, 0);
            _brush.EndPoint = new Vector2(0, 1);
            (float Offset, double Alpha)[] stops =
            [
                (0.00f, 1.0), (0.12f, Lerp(1.0, 0.5, 0.18)), (0.26f, Lerp(1.0, 0.5, 0.48)), (0.45f, 0.5),
                (0.62f, Lerp(0.5, 0.18, 0.36)), (0.80f, Lerp(0.5, 0.18, 0.68)), (1.00f, 0.18),
            ];
            foreach (var (offset, alpha) in stops)
            {
                _brush.ColorStops.Add(_compositor.CreateColorGradientStop(offset,
                    Color.FromArgb((byte)Math.Round(alpha * 255), 0, 0, 0)));
            }

            _visual = _compositor.CreateSpriteVisual();
            _visual.RelativeSizeAdjustment = Vector2.One;
            _visual.Brush = _brush;
            ElementCompositionPreview.SetElementChildVisual(host, _visual);
        }

        public void Show(Color? tint)
        {
            var snap = !_hasTint;
            _hasTint |= tint.HasValue;
            var target = tint ?? Colors.Black;
            foreach (var stop in _brush.ColorStops)
            {
                var color = Color.FromArgb(stop.Color.A, target.R, target.G, target.B);
                if (snap)
                {
                    stop.Color = color;
                    continue;
                }

                var animation = _compositor.CreateColorKeyFrameAnimation();
                animation.InsertExpressionKeyFrame(0, "this.StartingValue");
                animation.InsertKeyFrame(1, color, _easing);
                animation.Duration = TimeSpan.FromMilliseconds(500);
                stop.StartAnimation(nameof(CompositionColorGradientStop.Color), animation);
                animation.Dispose();
            }
        }

        public void Dispose()
        {
            ElementCompositionPreview.SetElementChildVisual(_host, null);
            _visual.Dispose();
            foreach (var stop in _brush.ColorStops)
            {
                stop.Dispose();
            }

            _brush.Dispose();
            _easing.Dispose();
        }
    }

    private static double Lerp(double from, double to, double t) => from + (to - from) * t;
}
