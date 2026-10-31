namespace Bale.Xbox.Services;

/// <summary>The existing focus visuals, not new focus behavior (FocusModifier.kt and the detail/calendar cards).</summary>
public enum RailFocusVisual
{
    Card,
    Episode,
    Calendar,
    Trailer,
    ExplorePoster,
}

/// <summary>Viewport geometry only. Kept free of XAML types so the padding contract can be tested on Linux.</summary>
internal static class CardRailMetrics
{
    public const float ArtScale = 1.05f;
    public const float EpisodeScale = 1.04f;
    public const float CalendarScale = 1.06f;
    public const double GlowOutset = 6;
    public const double SafetyMargin = 1;
    public const double GridTopInset = 12;

    public static double Headroom(double size, RailFocusVisual visual = RailFocusVisual.Card)
    {
        // The 2px poster ring starts at -3; its glow reaches -6. Both scale with the art.
        // Calendar's 2.5px ring reaches -2.5; episode/trailer borders are entirely inside.
        var (scale, outset) = visual switch
        {
            RailFocusVisual.Episode => (EpisodeScale, 0d),
            RailFocusVisual.Calendar => (CalendarScale, 2.5),
            RailFocusVisual.Trailer => (ArtScale, 0d),
            // TvExploreAllCard.kt lifts posters 1.10; neighbouring media cards still use their own scale.
            RailFocusVisual.ExplorePoster => (1.10f, GlowOutset),
            _ => (ArtScale, GlowOutset),
        };
        return Math.Ceiling(size * (scale - 1) / 2 + outset * scale + SafetyMargin);
    }

    public static (RailInsets Padding, RailInsets Margin) Layout(double width, double height, RailInsets inset,
        RailFocusVisual visual = RailFocusVisual.Card)
    {
        var horizontal = Headroom(width, visual);
        var vertical = Headroom(height, visual);
        var padding = new RailInsets(Math.Max(inset.Left, horizontal), Math.Max(inset.Top, vertical),
            Math.Max(inset.Right, horizontal), Math.Max(inset.Bottom, vertical));
        // Expand the viewport into the existing header/row gaps, not the art's resting position.
        // Never compensate horizontally: that would put the end card's zoom outside the page clip.
        var margin = new RailInsets(0, inset.Top - padding.Top, 0, inset.Bottom - padding.Bottom);
        return (padding, margin);
    }
}

internal readonly record struct RailInsets(double Left, double Top, double Right, double Bottom);
