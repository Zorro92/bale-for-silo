using Bale.Core.Browse;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media.Imaging;

namespace Bale.Xbox.Views;

// Item views handed to XAML lists. `partial` lets the CsWinRT AOT generator add the WinRT
// plumbing XAML needs to hold them (and List<T> of them) in ItemsSource.

/// <summary>A row of cards, sized like Silo's Android TV rails (RowDimens, TvSkylineSectionFeed).</summary>
public sealed partial class RowView
{
    /// <summary>Dense Home/Browse poster: 88x132 dp.</summary>
    public const double PosterWidth = 88;
    public const double PosterHeight = 132;

    /// <summary>16:9 Continue Watching / episode thumb: 180x100 dp.</summary>
    public const double ThumbWidth = 180;
    public const double ThumbHeight = 100;

    public RowView(MediaRow row)
    {
        Row = row;
        var landscape = row.Shape == CardShape.Landscape;
        IconVisibility = landscape ? Visibility.Visible : Visibility.Collapsed;
        Cards = row.Cards
            .Select(card => new CardView(card, row.Title, landscape ? ThumbWidth : PosterWidth, landscape ? ThumbHeight : PosterHeight, landscape))
            .ToList();
    }

    public MediaRow Row { get; }

    public string Title => Row.Title;

    /// <summary>Progress rows (Continue Watching, Next Up) get the play glyph before their title.</summary>
    public Visibility IconVisibility { get; }

    public IReadOnlyList<CardView> Cards { get; }

    /// <summary>The card focus last sat on in this row: Up/Down come back to it (TvMediaRow's focusRestorer).</summary>
    public CardView? LastFocused { get; set; }
}

public sealed partial class CardView
{
    public CardView(MediaCard card, string rowTitle, double width, double height, bool landscape)
    {
        Card = card;
        RowTitle = rowTitle;
        Width = width;
        Height = height;
        CaptionSpacing = landscape ? 7 : 11;
        ProgressPercent = (card.Progress ?? 0) * 100;
        ProgressVisibility = card.Progress is > 0 ? Visibility.Visible : Visibility.Collapsed;
        FallbackVisibility = card.ImageUrl is null ? Visibility.Visible : Visibility.Collapsed;
        OverlayCode = landscape ? card.EpisodeCode?.Replace(" ", " · ") ?? string.Empty : string.Empty;
        OverlayVisibility = OverlayCode.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SubtitleVisibility = string.IsNullOrEmpty(card.Subtitle) ? Visibility.Collapsed : Visibility.Visible;

        // CardOverlays: posters scale their badges by width / 185; wide cards by TvCardOverlayScale (0.7).
        var scale = landscape ? 0.7 : width / 185;
        CornerTop = card.CornerTop.Select(text => new BadgeView(text, scale)).ToList();
        CornerBottom = card.CornerBottom.Select(text => new BadgeView(text, scale)).ToList();
        CornerInset = new Thickness(8 * scale, 8 * scale, 0, (landscape ? 12 : 8) * scale);
        CornerGap = 4 * scale;

        if (card.ImageUrl is not null)
        {
            // Decode at the drawn size (x2 device pixels) so long rows stay light on memory.
            Image = Services.Artwork.Load(card.ImageUrl, width: (int)Math.Ceiling(width));
        }
    }

    public MediaCard Card { get; }

    /// <summary>Which row the card came from.</summary>
    public string RowTitle { get; }

    public string Title => Card.Title;

    /// <summary>The episode's title, or the year.</summary>
    public string Subtitle => Card.Subtitle ?? string.Empty;

    /// <summary>"S2 · E8" over the bottom-left of an episode thumb.</summary>
    public string OverlayCode { get; }

    public BitmapImage? Image { get; }

    /// <summary>Overlay badges in the art's top-left and bottom-left corners.</summary>
    public IReadOnlyList<BadgeView> CornerTop { get; }

    public IReadOnlyList<BadgeView> CornerBottom { get; }

    public Thickness CornerInset { get; }

    public double CornerGap { get; }

    public double Width { get; }

    public double Height { get; }

    /// <summary>Gap between the art and its caption: 11 under posters, 7 under thumbs.</summary>
    public double CaptionSpacing { get; }

    public double ProgressPercent { get; }

    /// <summary>The progress fill spans the bottom edge of the art.</summary>
    public double ProgressFillWidth => Math.Max(0, Width * ProgressPercent / 100);

    public Visibility ProgressVisibility { get; }

    public Visibility FallbackVisibility { get; }

    public Visibility OverlayVisibility { get; }

    public Visibility SubtitleVisibility { get; }
}

/// <summary>A top-bar tab: Home (no library tab) or a library type.</summary>
public sealed partial class TabView
{
    public TabView(string title, LibraryTab? libraryTab, string? page = null)
    {
        Title = title;
        LibraryTab = libraryTab;
        Page = page;
    }

    public string Title { get; }

    public LibraryTab? LibraryTab { get; }

    /// <summary>A fixed root that isn't a library: "foryou" or "calendar" (TvRootDestination).</summary>
    public string? Page { get; }
}

/// <summary>A library row in the cascade panel (guide §5.3).</summary>
public sealed partial class LibraryChoiceView
{
    public LibraryChoiceView(string id, string name, bool current)
    {
        Id = id;
        Name = name;
        IsCurrent = current;
    }

    public string Id { get; }

    public string Name { get; }

    public bool IsCurrent { get; }

    /// <summary>A checkmark on the current scope, a chevron on the others.</summary>
    public string Trailing => IsCurrent ? "" : "";
}

/// <summary>
/// One card overlay badge in the default "Classic" preset (OverlayPresetStyles.classic): an
/// uppercase capsule, black @ 60% with a white @ 15% hairline, scaled with the card.
/// </summary>
public sealed partial class BadgeView
{
    public BadgeView(string text, double scale)
    {
        Text = text;
        FontSize = 11 * 0.86 * scale;
        Padding = new Thickness(8 * scale, 3 * scale, 8 * scale, 3 * scale);
        Border = new Thickness(scale);
        Radius = new CornerRadius((FontSize * 1.33 + 6 * scale) / 2);
    }

    public string Text { get; }

    public double FontSize { get; }

    public Thickness Padding { get; }

    public Thickness Border { get; }

    public CornerRadius Radius { get; }
}
