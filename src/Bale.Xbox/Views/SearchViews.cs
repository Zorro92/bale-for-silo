using System.ComponentModel;
using Bale.Core.Browse;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media.Imaging;

namespace Bale.Xbox.Views;

/// <summary>A library-derived search chip. Partial for Native AOT's XAML collection projection.</summary>
public sealed partial class SearchFilterView(SearchMediaFilter filter) : INotifyPropertyChanged
{
    private bool _selected;

    public SearchMediaFilter Filter { get; } = filter;
    public string Label => Filter.Label;

    public bool Selected
    {
        get => _selected;
        set
        {
            // Reassert even the current selection: ToggleButton toggles itself before Click fires.
            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>TvSearchPeopleRow.kt reuses TvCastCard's 100 px circular photo and two-line name.</summary>
public sealed partial class SearchPersonView
{
    public SearchPersonView(SearchPerson person)
    {
        Person = person;
        PlaceholderVisibility = person.PhotoUrl is null ? Visibility.Visible : Visibility.Collapsed;
        if (person.PhotoUrl is not null)
        {
            Photo = Services.Artwork.Load(person.PhotoUrl, width: 100);
        }
    }

    public SearchPerson Person { get; }
    public string Name => Person.Name;
    public BitmapImage? Photo { get; }
    public Visibility PlaceholderVisibility { get; }
}
