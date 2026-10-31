using System.ComponentModel;

namespace Bale.Xbox.Views;

internal sealed record LibraryRecommendedEntry(string LibraryId, string Title, string? MediaScope);

public sealed partial class LibrarySectionChoiceView(string title, bool browse)
{
    public string Title { get; } = title;
    public bool Browse { get; } = browse;
    public string Icon => Browse ? "" : "";
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
    public string Text { get; private set; } = string.Empty;
    public double Width { get; private set; } = 8;
    public double Height { get; private set; } = 8;
    public bool Selected { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(bool expanded, string? prefix)
    {
        var selected = Letter switch { "All" => prefix is null, "#" => prefix == "0-9", _ => Letter == prefix };
        Text = expanded ? Letter : index % 2 == 0 || selected ? "•" : string.Empty;
        Width = expanded ? Letter == "All" ? 56 : 42 : 8;
        Height = expanded ? Letter == "All" ? 28 : 42 : 8;
        Selected = expanded && selected;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}
