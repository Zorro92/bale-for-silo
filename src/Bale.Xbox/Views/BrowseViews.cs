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
            .Select((card, index) => new CardView(card, row.Title, landscape ? ThumbWidth : PosterWidth, landscape ? ThumbHeight : PosterHeight, landscape)
            {
                IsFirst = index == 0,
                IsLast = index == row.Cards.Count - 1,
            })
            .ToList();
    }

    public MediaRow Row { get; }

    public string Title => Row.Title;

    /// <summary>Progress rows (Continue Watching, Next Up) get the play glyph before their title.</summary>
    public Visibility IconVisibility { get; }

    public IReadOnlyList<CardView> Cards { get; }
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

        if (card.ImageUrl is not null)
        {
            // Decode at the drawn size (x2 device pixels) so long rows stay light on memory.
            Image = new BitmapImage { DecodePixelType = DecodePixelType.Logical, DecodePixelWidth = (int)Math.Ceiling(width), UriSource = card.ImageUrl };
        }
    }

    public MediaCard Card { get; }

    /// <summary>Which row the card came from.</summary>
    public string RowTitle { get; }

    /// <summary>Ends of the rail: Left/Right stop here instead of wandering into another row.</summary>
    public bool IsFirst { get; init; }

    public bool IsLast { get; init; }

    public string Title => Card.Title;

    /// <summary>The episode's title, or the year.</summary>
    public string Subtitle => Card.Subtitle ?? string.Empty;

    /// <summary>"S2 · E8" over the bottom-left of an episode thumb.</summary>
    public string OverlayCode { get; }

    public BitmapImage? Image { get; }

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
    public TabView(string title, LibraryTab? libraryTab)
    {
        Title = title;
        LibraryTab = libraryTab;
    }

    public string Title { get; }

    public LibraryTab? LibraryTab { get; }
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
