using Bale.Core.Settings;
using Windows.Media.ClosedCaptioning;
using Windows.UI;

namespace Bale.Xbox.Services;

/// <summary>
/// The console's closed-caption settings (Settings › Accessibility › Closed captions), read from UWP
/// ClosedCaptionProperties for Settings › Subtitles' Use Device Settings (Bale.Core SystemCaptionStyle maps them onto the
/// appearance). "Default" values are left to the app. Null when the API cannot be read; never throws.
/// </summary>
internal static class SystemCaptions
{
    internal static SystemCaptionStyle? Read()
    {
        try
        {
            return new SystemCaptionStyle(
                ClosedCaptionProperties.FontColor == ClosedCaptionColor.Default ? null : Hex(ClosedCaptionProperties.ComputedFontColor),
                Opacity(ClosedCaptionProperties.FontOpacity),
                ClosedCaptionProperties.FontSize switch
                {
                    ClosedCaptionSize.FiftyPercent => (double?)0.5,
                    ClosedCaptionSize.OneHundredPercent => 1.0,
                    ClosedCaptionSize.OneHundredFiftyPercent => 1.5,
                    ClosedCaptionSize.TwoHundredPercent => 2.0,
                    _ => null,
                },
                ClosedCaptionProperties.FontStyle switch
                {
                    ClosedCaptionStyle.MonospacedWithSerifs => SystemCaptionFont.MonospacedWithSerifs,
                    ClosedCaptionStyle.ProportionalWithSerifs => SystemCaptionFont.ProportionalWithSerifs,
                    ClosedCaptionStyle.MonospacedWithoutSerifs => SystemCaptionFont.MonospacedWithoutSerifs,
                    ClosedCaptionStyle.ProportionalWithoutSerifs => SystemCaptionFont.ProportionalWithoutSerifs,
                    ClosedCaptionStyle.Casual => SystemCaptionFont.Casual,
                    ClosedCaptionStyle.Cursive => SystemCaptionFont.Cursive,
                    ClosedCaptionStyle.SmallCapitals => SystemCaptionFont.SmallCapitals,
                    _ => SystemCaptionFont.Default,
                },
                ClosedCaptionProperties.FontEffect switch
                {
                    ClosedCaptionEdgeEffect.None => SystemCaptionEdge.None,
                    ClosedCaptionEdgeEffect.Raised => SystemCaptionEdge.Raised,
                    ClosedCaptionEdgeEffect.Depressed => SystemCaptionEdge.Depressed,
                    ClosedCaptionEdgeEffect.Uniform => SystemCaptionEdge.Uniform,
                    ClosedCaptionEdgeEffect.DropShadow => SystemCaptionEdge.DropShadow,
                    _ => SystemCaptionEdge.Default,
                },
                ClosedCaptionProperties.BackgroundColor == ClosedCaptionColor.Default ? null : Hex(ClosedCaptionProperties.ComputedBackgroundColor),
                Opacity(ClosedCaptionProperties.BackgroundOpacity));
        }
        catch (Exception ex)
        {
            Log.Info($"System captions: read FAILS ({ex.GetType().Name} 0x{unchecked((uint)ex.HResult):X8})");
            return null;
        }
    }

    private static int? Opacity(ClosedCaptionOpacity opacity) => opacity switch
    {
        ClosedCaptionOpacity.OneHundredPercent => 100,
        ClosedCaptionOpacity.SeventyFivePercent => 75,
        ClosedCaptionOpacity.TwentyFivePercent => 25,
        ClosedCaptionOpacity.ZeroPercent => 0,
        _ => null,
    };

    private static string Hex(Color color) => $"#{color.R:x2}{color.G:x2}{color.B:x2}";
}
