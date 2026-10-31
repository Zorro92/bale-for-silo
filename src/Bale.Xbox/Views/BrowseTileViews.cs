using System.ComponentModel;
using Bale.Core;
using Bale.Core.Browse;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Bale.Xbox.Views;

/// <summary>Rail focus treats the section end card as a slot, not as a playable media item.</summary>
public abstract partial class BrowseTileView
{
    public abstract string Key { get; }
}

public sealed partial class ExploreAllView : BrowseTileView, INotifyPropertyChanged
{
    // TvMediaRow.kt: content IDs cannot contain NUL, so this identity survives row refreshes safely.
    public override string Key => "\0explore-all";
    public SectionBrowseSource Source { get; private set; } = null!;
    public double Width { get; private set; }
    public double Height { get; private set; }
    public float FocusScale => Height < Width ? 1.04f : 1.10f;
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(SectionBrowseSource source, double width, double height)
    {
        Source = source;
        Width = width;
        Height = height;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}

public sealed partial class BrowseTileTemplateSelector : DataTemplateSelector
{
    public DataTemplate MediaTemplate { get; set; } = null!;
    public DataTemplate ExploreTemplate { get; set; } = null!;
    protected override DataTemplate SelectTemplateCore(object item) => item is ExploreAllView ? ExploreTemplate : MediaTemplate;
    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}

/// <summary>
/// Navigation parameter for CatalogGridPage. Create one per grid visit; the normal Frame journal
/// retains its pages and return-card identity across details. A source may own mutable query state
/// and ask the page to ReloadAsync when it changes. Optional source chrome is installed by the
/// page's ConfigureSource partial in ToolbarSlot (scrolling header) or SourceOverlay (full page).
/// Never reuse an entry for a different client or acting profile.
/// </summary>
public sealed class CatalogGridEntry(ICatalogGridSource source, SiloClient client)
{
    public ICatalogGridSource Source { get; } = source;
    public SiloClient Client { get; } = client;
    public string? ProfileId { get; } = client.ProfileId;
    public CatalogGridResults Results { get; } = new();
    public string? ReturnItemId { get; set; }
    public bool IsCurrent(SiloClient? current) => ReferenceEquals(Client, current) && current?.ProfileId == ProfileId;
}
