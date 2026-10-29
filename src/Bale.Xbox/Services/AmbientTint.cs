using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.UI;
using Windows.UI.Xaml.Media;

namespace Bale.Xbox.Services;

/// <summary>
/// The page tint behind the marquee, ported from Silo's Android TV app (AmbientBackdropTint,
/// TvRootHeroBackdrop): the backdrop's average colour with its luminance pulled to 0.22, laid as a
/// diagonal wash, strongest top-right behind the art and fading toward the bottom-left.
/// </summary>
internal static class AmbientTint
{
    private const double TargetLuminance = 0.22;

    private static readonly HttpClient Http = new();
    private static readonly Dictionary<Uri, Color> Cache = new();

    public static async Task<Color?> SampleAsync(Uri image)
    {
        if (Cache.TryGetValue(image, out var cached))
        {
            return cached;
        }

        try
        {
            var bytes = await Http.GetByteArrayAsync(image);
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

    /// <summary>The wash: tint at 1.0 top-right, 0.5 mid, 0.18 bottom-left, with the Android app's smoothed stops.</summary>
    public static LinearGradientBrush Wash(Color tint)
    {
        var brush = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(1, 0), EndPoint = new Windows.Foundation.Point(0, 1) };
        (double Offset, double Alpha)[] stops =
        [
            (0.00, 1.0), (0.12, Lerp(1.0, 0.5, 0.18)), (0.26, Lerp(1.0, 0.5, 0.48)), (0.45, 0.5),
            (0.62, Lerp(0.5, 0.18, 0.36)), (0.80, Lerp(0.5, 0.18, 0.68)), (1.00, 0.18),
        ];
        foreach (var (offset, alpha) in stops)
        {
            brush.GradientStops.Add(new GradientStop { Offset = offset, Color = Color.FromArgb((byte)Math.Round(alpha * 255), tint.R, tint.G, tint.B) });
        }

        return brush;
    }

    /// <summary>The wash colour as it looks over black at a given strength, for fading the art into it.</summary>
    public static Color Over(Color tint, double alpha) =>
        Color.FromArgb(255, (byte)(tint.R * alpha), (byte)(tint.G * alpha), (byte)(tint.B * alpha));

    private static double Lerp(double from, double to, double t) => from + (to - from) * t;
}
