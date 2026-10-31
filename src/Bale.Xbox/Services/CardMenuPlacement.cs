namespace Bale.Xbox.Services;

/// <summary>TvMediaCardActions.kt's AboveAnchorPopupPositionProvider, in effective pixels.</summary>
internal static class CardMenuPlacement
{
    public static (double X, double Y) Position(double anchorLeft, double anchorTop, double anchorWidth,
        double windowWidth, double windowHeight, double menuWidth, double menuHeight)
    {
        // The official anchor is a full-width, zero-height Box before the card, not its artwork/caption bounds.
        const double margin = 8;
        var x = Math.Clamp(anchorLeft + (anchorWidth - menuWidth) / 2, 0, Math.Max(0, windowWidth - menuWidth));
        var above = anchorTop - menuHeight - margin;
        var y = above >= 0 ? above : Math.Min(anchorTop + margin, Math.Max(0, windowHeight - menuHeight));
        return (x, y);
    }
}
