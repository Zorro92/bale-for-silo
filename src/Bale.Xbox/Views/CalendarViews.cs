using System.Collections.ObjectModel;
using System.ComponentModel;
using Bale.Core.Browse;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media.Imaging;

namespace Bale.Xbox.Views;

// Partial for the Native AOT XAML collection projections, as on Browse and Search.
public sealed partial class CalendarFilterView(CalendarFilter filter) : INotifyPropertyChanged
{
    private bool _selected;

    public CalendarFilter Filter { get; } = filter;
    public string Label => Filter.ToString();

    public bool Selected
    {
        get => _selected;
        set
        {
            // A ToggleButton changes itself before Click; reassert even the active segment.
            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>One day shared by the week strip and its shelf; surviving cards keep their containers.</summary>
public sealed partial class CalendarDayView : INotifyPropertyChanged
{
    public DateOnly Date { get; private set; }
    public string Header { get; private set; } = string.Empty;
    public string Weekday => Date.ToString("ddd");
    public string Number => Date.Day.ToString();
    public string AccessibleDate => Date.ToString("D");
    public bool Selected { get; private set; }
    public bool Today { get; private set; }
    public bool Focused { get; private set; }
    public Visibility TodayOutlineVisibility => Today && !Selected && !Focused ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RestingDotVisibility => Cards.Count > 0 && !Selected && !Focused ? Visibility.Visible : Visibility.Collapsed;
    public Visibility InvertedDotVisibility => Cards.Count > 0 && (Selected || Focused) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EventVisibility => Cards.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyVisibility => Cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public double HeaderOpacity => Cards.Count == 0 ? 0.45 : 1;
    public ObservableCollection<CalendarCardView> Cards { get; } = new();
    public CalendarCardView? LastFocused { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(CalendarDay day, DateOnly today, DateOnly selected)
    {
        if (Date != day.Date)
        {
            Cards.Clear();
            LastFocused = null;
        }

        Date = day.Date;
        Header = day.Header(today);
        Today = Date == today;
        Selected = Date == selected;
        var rememberedIndex = LastFocused is { } last ? Cards.IndexOf(last) : 0;
        var index = 0;
        foreach (var item in day.Items)
        {
            var existing = Cards.Skip(index).FirstOrDefault(card => card.Item.ContentId == item.ContentId);
            if (existing is null)
            {
                Cards.Insert(index, new CalendarCardView(item));
            }
            else
            {
                var oldIndex = Cards.IndexOf(existing);
                if (oldIndex != index)
                {
                    Cards.Move(oldIndex, index);
                }

                existing.Update(item);
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

    public void Select(DateOnly selected)
    {
        Selected = Date == selected;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public void SetFocused(bool focused)
    {
        Focused = focused;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}

public sealed partial class CalendarCardView : INotifyPropertyChanged
{
    public CalendarCardView(CalendarItem item) => Update(item);

    public CalendarItem Item { get; private set; } = null!;
    public string Title => Item.Title;
    public string Subtitle => Item.Subtitle ?? string.Empty;
    public string AirTime => Item.AirTime ?? string.Empty;
    public string AccessibleName => string.Join(" · ", new[] { Title, Subtitle, AirTime }.Where(value => value.Length > 0));
    public BitmapImage? Image { get; private set; }
    public IReadOnlyList<CalendarBadgeView> Badges { get; private set; } = [];
    public double WatchedOpacity => Item.Watched ? 0.65 : 1;
    public Visibility WatchedVisibility => Item.Watched ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FallbackVisibility => Item.PosterUrl is null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SubtitleVisibility => Item.Subtitle is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility AirTimeVisibility => Item.AirTime is null ? Visibility.Collapsed : Visibility.Visible;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(CalendarItem item)
    {
        var changedImage = Item?.PosterUrl != item.PosterUrl;
        Item = item;
        if (changedImage)
        {
            Image = item.PosterUrl is { } poster ? Services.Artwork.Load(poster) : null;
        }

        Badges = item.Badges.Select(label => new CalendarBadgeView(label)).ToArray();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}

public sealed partial class CalendarBadgeView(string text)
{
    public string Text { get; } = text;
}

public sealed partial class CalendarActionView(string label, CalendarFilter? filter = null)
{
    public string Label { get; } = label;
    public CalendarFilter? Filter { get; } = filter;
}
