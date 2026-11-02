using System.ComponentModel;
using Bale.Core.Browse;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Bale.Xbox.Views;

internal sealed record LibraryRecommendedEntry(string LibraryId, string Title, string? MediaScope);

public sealed partial class LibrarySectionChoiceView(LibrarySection section)
{
    public LibrarySection Section { get; } = section;
    public string Title => Section.ToString();
    public Visibility RecommendedVisibility => Section == LibrarySection.Recommended ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BrowseVisibility => Section == LibrarySection.Browse ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CollectionsVisibility => Section == LibrarySection.Collections ? Visibility.Visible : Visibility.Collapsed;
}

public sealed partial class LibraryPanelRow(string key, string title, Action choose) : INotifyPropertyChanged
{
    public string Key { get; } = key;
    public string Title { get; } = title;
    public Action Choose { get; } = choose;
    public string Detail { get; private set; } = string.Empty;
    public string Mark { get; private set; } = string.Empty;
    public double RowOpacity { get; private set; } = 1;
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(string detail = "", string mark = "", bool muted = false)
    {
        Detail = detail;
        Mark = mark;
        RowOpacity = muted ? 0.45 : 1;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}

/// <summary>TvAlphabetRail.kt: each letter keeps ONE focusable node while the dots expand into chips.</summary>
public sealed partial class LibraryAlphabetEntry(string letter, int index) : INotifyPropertyChanged
{
    public string Letter { get; } = letter;
    public bool ShowCollapsedDot { get; } = index % 2 == 0;
    public bool Selected { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(string? prefix)
    {
        // TvAlphabetRail.kt: the selected dot stays visible even between the alternating ordinary dots.
        Selected = Letter switch { "All" => prefix is null, "#" => prefix is "0-9" or "#", _ => Letter == prefix };
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}

/// <summary>TvAlphabetRail.kt: All is an app pill, not a square monospaced letter chip.</summary>
public sealed partial class LibraryAlphabetTemplateSelector : DataTemplateSelector
{
    public DataTemplate AllTemplate { get; set; } = null!;
    public DataTemplate LetterTemplate { get; set; } = null!;
    protected override DataTemplate SelectTemplateCore(object item) => item is LibraryAlphabetEntry { Letter: "All" } ? AllTemplate : LetterTemplate;
    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
