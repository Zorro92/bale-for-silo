using System.Collections.ObjectModel;
using Bale.Core;
using Bale.Core.Browse;
using Bale.Xbox.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Bale.Xbox.Views;

internal sealed class LibraryCollectionsEntry(string libraryId, string? mediaScope, SiloClient client)
{
    public string LibraryId { get; } = libraryId;
    public string? MediaScope { get; } = mediaScope;
    public SiloClient Client { get; } = client;
    public string? ProfileId { get; } = client.ProfileId;
    public IReadOnlyList<LibraryCollectionSection>? Sections { get; set; }
    public string? LastFocusedId { get; set; }
    public bool IsCurrent => ReferenceEquals(Client, Session.Client) && Client.ProfileId == ProfileId;
}

/// <summary>screens/library/TvLibraryDetailScreen.kt TvCollectionCard: the shared poster focus envelope, no item count.</summary>
public sealed partial class LibraryCollectionCardView
{
    public LibraryCollectionCardView(LibraryCollectionCell cell, double width)
    {
        Cell = cell;
        var collection = cell.Collection;
        Poster = new CardView(new MediaCard(collection.Id, "collection", collection.Title, null, collection.Poster,
            null, null, null, null, null, null, null, null, [], null, null, null), string.Empty, width, width * 1.5, false)
        {
            GridFocusVisual = RailFocusVisual.ExplorePoster,
        };
    }

    public LibraryCollectionCell Cell { get; }
    public CardView Poster { get; }
}

public sealed partial class LibraryCollectionRowView(string? header, IReadOnlyList<LibraryCollectionCardView> cards)
{
    public string Header { get; } = header ?? string.Empty;
    public bool IsHeader { get; } = header is not null;
    // An interface-typed list bound by x:Bind reached the GridView empty under Native AOT; the
    // concrete ObservableCollection is what CalendarViews/BrowseViews bind successfully.
    public ObservableCollection<LibraryCollectionCardView> Cards { get; } = new(cards);
}

public sealed partial class LibraryCollectionRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate HeaderTemplate { get; set; } = null!;
    public DataTemplate CardsTemplate { get; set; } = null!;
    protected override DataTemplate SelectTemplateCore(object item) => item is LibraryCollectionRowView { IsHeader: true } ? HeaderTemplate : CardsTemplate;
    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
