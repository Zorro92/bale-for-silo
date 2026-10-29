using Bale.Core.Browse;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media.Imaging;

namespace Bale.Xbox.Views;

// Item views handed to XAML lists. `partial` lets the CsWinRT AOT generator add the WinRT
// plumbing XAML needs to hold them (and List<T> of them) in ItemsSource.

/// <summary>A row of cards. Sizes are Skyline's (guide §5.6, Android RowDimens) in effective pixels.</summary>
public sealed partial class RowView
{
    /// <summary>Dense Home/Browse poster: tvOS 176 pt, Android 88 dp.</summary>
    public const double PosterWidth = 88;
    public const double PosterHeight = 132;

    /// <summary>Continue Watching thumb: 372x209 mockup px.</summary>
    public const double ThumbWidth = 186;
    public const double ThumbHeight = 104.5;

    public RowView(MediaRow row)
    {
        Row = row;
        var landscape = row.Shape == CardShape.Landscape;
        Cards = row.Cards
            .Select(card => new CardView(card, row.Title, landscape ? ThumbWidth : PosterWidth, landscape ? ThumbHeight : PosterHeight, landscape))
            .ToList();
    }

    public MediaRow Row { get; }

    public string Title => Row.Title;

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
        CaptionVisibility = landscape ? Visibility.Visible : Visibility.Collapsed;
        ProgressPercent = (card.Progress ?? 0) * 100;
        ProgressVisibility = card.Progress is > 0 ? Visibility.Visible : Visibility.Collapsed;
        FallbackVisibility = card.ImageUrl is null ? Visibility.Visible : Visibility.Collapsed;
        CaptionDetail = string.Join(" · ", new[]
        {
            card.EpisodeCode?.Replace(" ", " · "),
            card.RemainingMinutes is > 0 ? $"{card.RemainingMinutes}m left" : null,
        }.Where(part => part is not null));

        if (card.ImageUrl is not null)
        {
            // Decode at the drawn size (x2 device pixels) so long rows stay light on memory.
            Image = new BitmapImage { DecodePixelType = DecodePixelType.Logical, DecodePixelWidth = (int)Math.Ceiling(width), UriSource = card.ImageUrl };
        }
    }

    public MediaCard Card { get; }

    /// <summary>The marquee eyebrow: which row the card came from.</summary>
    public string RowTitle { get; }

    public string Title => Card.Title;

    /// <summary>The year, or "S2 · E5 · Title" for episodes; shown under grid posters.</summary>
    public string Subtitle => Card.Subtitle ?? string.Empty;

    /// <summary>Right-aligned under a thumb: "S2 · E7 · 23m left".</summary>
    public string CaptionDetail { get; }

    public BitmapImage? Image { get; }

    public double Width { get; }

    public double Height { get; }

    public Visibility CaptionVisibility { get; }

    public double ProgressPercent { get; }

    /// <summary>Progress fill width: the bar is inset 7 px each side of the thumb (guide §5.6).</summary>
    public double ProgressFillWidth => Math.Max(0, (Width - 14) * ProgressPercent / 100);

    public Visibility ProgressVisibility { get; }

    public Visibility FallbackVisibility { get; }
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
