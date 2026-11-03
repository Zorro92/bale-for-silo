using System.Collections.ObjectModel;
using System.ComponentModel;
using Bale.Core.Requests;
using Bale.Xbox.Services;
using Windows.UI.Xaml.Media.Imaging;

namespace Bale.Xbox.Views;

// XAML collection objects are partial for the Native AOT collection projections.
public sealed partial class RequestCardView(RequestTitle item) : INotifyPropertyChanged
{
    public RequestTitle Item { get; private set; } = item;
    public BitmapImage? Image { get; private set; } = item.PosterUrl is { } poster ? Artwork.Load(poster, 88, 132) : null;
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(RequestTitle item)
    {
        if (Item.PosterUrl != item.PosterUrl)
        {
            Image = item.PosterUrl is { } poster ? Artwork.Load(poster, 88, 132) : null;
        }
        Item = item;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}

/// <summary>TvRequestsPage.kt: row-unique keys and surviving instances keep per-row focus across refreshes.</summary>
public sealed partial class RequestRowView(RequestRow row) : INotifyPropertyChanged
{
    public string Key { get; } = row.Key;
    public string Title { get; private set; } = row.Title;
    public string? Eyebrow { get; private set; } = row.Eyebrow;
    public Windows.UI.Xaml.Visibility EyebrowVisibility => Eyebrow is null ? Windows.UI.Xaml.Visibility.Collapsed : Windows.UI.Xaml.Visibility.Visible;
    public ObservableCollection<RequestCardView> Cards { get; } = new(row.Items.Select(item => new RequestCardView(item)));
    public RequestCardView? LastFocused { get; set; }
    internal Rail? Rail { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(RequestRow row)
    {
        Title = row.Title;
        Eyebrow = row.Eyebrow;
        var index = 0;
        foreach (var item in row.Items)
        {
            var existing = Cards.Skip(index).FirstOrDefault(card => card.Item.Key == item.Key);
            if (existing is null) Cards.Insert(index, new RequestCardView(item));
            else
            {
                var previous = Cards.IndexOf(existing);
                if (previous != index) Cards.Move(previous, index);
                existing.Update(item);
            }
            index++;
        }
        while (Cards.Count > index) Cards.RemoveAt(Cards.Count - 1);
        if (LastFocused is not null && !Cards.Contains(LastFocused)) LastFocused = Cards.FirstOrDefault();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}

public sealed record RequestDetailEntry(string MediaType, long TmdbId);
