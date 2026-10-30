using System.Collections.ObjectModel;
using System.ComponentModel;
using Bale.Core.Browse;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media.Imaging;

namespace Bale.Xbox.Views;

// Item views handed to XAML lists. `partial` lets the CsWinRT AOT generator add the WinRT
// plumbing XAML needs to hold them (and List<T> of them) in ItemsSource.

/// <summary>A row of cards, sized like Silo's Android TV rails (RowDimens, TvSkylineSectionFeed).</summary>
public sealed partial class RowView : INotifyPropertyChanged
{
    /// <summary>Dense Home/Browse poster: 88x132 dp.</summary>
    public const double PosterWidth = 88;
    public const double PosterHeight = 132;

    /// <summary>16:9 Continue Watching / episode thumb: 180x100 dp.</summary>
    public const double ThumbWidth = 180;
    public const double ThumbHeight = 100;

    public RowView(MediaRow row) => Update(row);

    public MediaRow Row { get; private set; } = null!;

    public string Title => Row.Title;

    /// <summary>Progress rows (Continue Watching, Next Up) get the play glyph before their title.</summary>
    public Visibility IconVisibility => Row.Shape == CardShape.Landscape ? Visibility.Visible : Visibility.Collapsed;

    public ObservableCollection<CardView> Cards { get; } = new();

    /// <summary>The card focus last sat on in this row: Up/Down come back to it (TvMediaRow's focusRestorer).</summary>
    public CardView? LastFocused { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>TvHomeScreen.kt refreshes quietly on resume: surviving cards keep their views and containers.</summary>
    public void Update(MediaRow row)
    {
        var rememberedIndex = LastFocused is { } last ? Cards.IndexOf(last) : -1;
        Row = row;
        var landscape = row.Shape == CardShape.Landscape;
        var width = landscape ? ThumbWidth : PosterWidth;
        var height = landscape ? ThumbHeight : PosterHeight;
        var index = 0;
        // TvMediaRow.kt deduplicates by contentId before displaying the rail.
        foreach (var card in row.Cards.DistinctBy(card => card.ContentId))
        {
            var existing = Cards.Skip(index).FirstOrDefault(view => view.Card.ContentId == card.ContentId);
            if (existing is null)
            {
                Cards.Insert(index, new CardView(card, row.Title, width, height, landscape));
            }
            else
            {
                var oldIndex = Cards.IndexOf(existing);
                if (oldIndex != index)
                {
                    Cards.Move(oldIndex, index);
                }

                existing.Update(card, row.Title, width, height, landscape);
            }

            index++;
        }

        while (Cards.Count > index)
        {
            Cards.RemoveAt(Cards.Count - 1);
        }

        if (LastFocused is not null && !Cards.Contains(LastFocused))
        {
            LastFocused = Cards.Count > 0 ? Cards[Math.Clamp(rememberedIndex, 0, Cards.Count - 1)] : null;
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}

public sealed partial class CardView : INotifyPropertyChanged
{
    public CardView(MediaCard card, string rowTitle, double width, double height, bool landscape) =>
        Update(card, rowTitle, width, height, landscape);

    public void Update(MediaCard card, string rowTitle, double width, double height, bool landscape)
    {
        var imageChanged = Card?.ImageUrl != card.ImageUrl || Width != width;
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

        if (imageChanged)
        {
            // Decode at the drawn size (x2 device pixels) so long rows stay light on memory.
            Image = card.ImageUrl is { } image ? Services.Artwork.Load(image, width: (int)Math.Ceiling(width)) : null;
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MediaCard Card { get; private set; } = null!;

    /// <summary>Which row the card came from.</summary>
    public string RowTitle { get; private set; } = string.Empty;

    public string Title => Card.Title;

    /// <summary>The episode's title, or the year.</summary>
    public string Subtitle => Card.Subtitle ?? string.Empty;

    /// <summary>"S2 · E8" over the bottom-left of an episode thumb.</summary>
    public string OverlayCode { get; private set; } = string.Empty;

    public BitmapImage? Image { get; private set; }

    /// <summary>Overlay badges in the art's top-left and bottom-left corners.</summary>
    public IReadOnlyList<BadgeView> CornerTop { get; private set; } = [];

    public IReadOnlyList<BadgeView> CornerBottom { get; private set; } = [];

    public Thickness CornerInset { get; private set; }

    public double CornerGap { get; private set; }

    public double Width { get; private set; }

    public double Height { get; private set; }

    /// <summary>Gap between the art and its caption: 11 under posters, 7 under thumbs.</summary>
    public double CaptionSpacing { get; private set; }

    public double ProgressPercent { get; private set; }

    /// <summary>The progress fill spans the bottom edge of the art.</summary>
    public double ProgressFillWidth => Math.Max(0, Width * ProgressPercent / 100);

    public Visibility ProgressVisibility { get; private set; }

    public Visibility FallbackVisibility { get; private set; }

    public Visibility OverlayVisibility { get; private set; }

    public Visibility SubtitleVisibility { get; private set; }
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
