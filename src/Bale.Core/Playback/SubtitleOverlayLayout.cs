namespace Bale.Core.Playback;

/// <summary>
/// Where the subtitle overlay draws: the swap chain covers the whole player panel, and the video rectangle is
/// MediaPlayerElement's Stretch="Uniform" fit inside it. Everything is in physical pixels, because the
/// overlay's swap chain is sized in them (panel size in effective pixels × the panel's composition scale).
/// </summary>
public readonly record struct SubtitleOverlayLayout(uint Width, uint Height, float ScaleX, float ScaleY,
    float VideoX, float VideoY, float VideoWidth, float VideoHeight)
{
    /// <summary>
    /// <paramref name="panelWidth"/>/<paramref name="panelHeight"/> are effective pixels (ActualWidth/Height).
    /// An unknown natural video size (0) makes the video rectangle the whole panel.
    /// </summary>
    public static SubtitleOverlayLayout Compute(double panelWidth, double panelHeight, double scaleX, double scaleY,
        uint videoWidth, uint videoHeight)
    {
        var sx = Scale(scaleX);
        var sy = Scale(scaleY);
        var width = Pixels(panelWidth * sx);
        var height = Pixels(panelHeight * sy);
        if (videoWidth == 0 || videoHeight == 0)
            return new(width, height, (float)sx, (float)sy, 0, 0, width, height);

        // Uniform: the largest rectangle with the video's aspect ratio, centered (letterbox or pillarbox).
        var fit = Math.Min((double)width / videoWidth, (double)height / videoHeight);
        var fitWidth = videoWidth * fit;
        var fitHeight = videoHeight * fit;
        return new(width, height, (float)sx, (float)sy,
            (float)((width - fitWidth) / 2), (float)((height - fitHeight) / 2), (float)fitWidth, (float)fitHeight);
    }

    private static double Scale(double value) => double.IsFinite(value) && value > 0 ? value : 1;

    /// <summary>A swap chain needs at least one pixel, also before the first layout pass.</summary>
    private static uint Pixels(double value) =>
        double.IsFinite(value) && value >= 1 ? (uint)Math.Min(Math.Round(value), 16384) : 1;
}
