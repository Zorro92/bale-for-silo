using Bale.Core.Home;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media.Imaging;

namespace Bale.Xbox.Views;

/// <summary>A home row as the page draws it. Card sizes are in effective pixels (Xbox lays out at 960x540).</summary>
public sealed class RowView
{
    public RowView(HomeRow row)
    {
        Row = row;
        var landscape = row.Shape == CardShape.Landscape;
        CardWidth = landscape ? 248 : 140;
        CardHeight = landscape ? 140 : 210;
        Cards = row.Cards.Select(card => new CardView(card, CardWidth, CardHeight, landscape)).ToList();
    }

    public HomeRow Row { get; }

    public string Title => Row.Title;

    public double CardWidth { get; }

    public double CardHeight { get; }

    public IReadOnlyList<CardView> Cards { get; }
}

public sealed class CardView
{
    public CardView(MediaCard card, double width, double height, bool landscape)
    {
        Card = card;
        Width = width;
        Height = height;
        ShowCaption = landscape ? Visibility.Visible : Visibility.Collapsed;
        ProgressPercent = (card.Progress ?? 0) * 100;
        ProgressVisibility = card.Progress is > 0 ? Visibility.Visible : Visibility.Collapsed;
        FallbackVisibility = card.ImageUrl is null ? Visibility.Visible : Visibility.Collapsed;
        if (card.ImageUrl is not null)
        {
            // Decode at the size drawn, not the source size, to keep memory down on long rows.
            Image = new BitmapImage { DecodePixelType = DecodePixelType.Logical, DecodePixelWidth = (int)width, UriSource = card.ImageUrl };
        }
    }

    public MediaCard Card { get; }

    public string Title => Card.Title;

    public string Caption => Card.Subtitle ?? string.Empty;

    public BitmapImage? Image { get; }

    public double Width { get; }

    public double Height { get; }

    public Visibility ShowCaption { get; }

    public double ProgressPercent { get; }

    public Visibility ProgressVisibility { get; }

    public Visibility FallbackVisibility { get; }
}
