using System.Collections.ObjectModel;
using Bale.Core;
using Bale.Core.Browse;
using Bale.Core.Detail;
using Bale.Xbox.Services;
using Bale.Xbox.Views;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace Bale.Xbox;

/// <summary>
/// The collections row of a movie or series page (Android TV cb2c32d8a: TvItemCollectionsRow.kt, TvItemDetailScreen.kt,
/// TvItemDetailViewModel.loadCollections). It is read after the hero, never in its way, and shows nothing until its read
/// answers; a later read that fails keeps it, and every return to the page reads it again.
/// </summary>
public sealed partial class DetailPage
{
    // TvItemCollectionsRow.kt: tvCardWidth posters, 2:3 (SkylineDetailRelatedWidth/Height).
    private const double CollectionCardWidth = 130;
    private const double CollectionCardHeight = 195;

    private readonly Rail _collectionPreviewRail;
    private readonly Rail _collectionCardsRail;
    private readonly ObservableCollection<BrowseTileView> _collectionTiles = new();
    private List<CardView> _collectionCardViews = [];
    private ItemCollectionsRow? _collections;
    private CancellationTokenSource? _collectionsLoad;
    private bool _collectionsShown;
    // The collections-row card a page was opened from (TvDetailReturnRail.Collections), by its ReturnIndex key.
    private string? _returnCollectionsKey;

    private Rail CollectionsRail => _collections?.IsPreview == true ? _collectionPreviewRail : _collectionCardsRail;

    private async Task LoadCollectionsAsync(ItemDetail detail)
    {
        _collectionsLoad?.Cancel();
        _collectionsLoad?.Dispose();
        var load = _collectionsLoad = new CancellationTokenSource();
        if (!ItemCollections.Asks(detail.Type) || Session.Client is not { } client) return;
        try
        {
            var row = await client.Detail.CollectionsAsync(detail, load.Token);
            if (!load.IsCancellationRequested && _active && _detail?.ContentId == detail.ContentId) ShowCollections(row);
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // TvItemDetailViewModel.loadCollections: a failed read keeps the row on screen (none on a first load).
            Log.Error("Detail collections", ex);
        }
    }

    /// <summary>
    /// TvItemCollectionsRowSection: the collection's name or "Also in" over one of two rails. A read that keeps the same
    /// cards updates them in place, so focus and the rail's position stay; one that changes them while the row has focus
    /// puts focus back on the same card, or on the rail.
    /// </summary>
    private void ShowCollections(ItemCollectionsRow? row)
    {
        var focused = FocusManager.GetFocusedElement() as DependencyObject;
        var hadFocus = _collectionsShown && IsWithin(focused, CollectionsSection);
        if (row is null)
        {
            // The row went away under focus: go to the section above it, as Up would.
            if (hadFocus) MoveSectionFocus(focused, FocusNavigationDirection.Up);
            _collections = null;
            _collectionTiles.Clear();
            _collectionCardViews = [];
            CollectionCards.ItemsSource = null;
            PlaceCollectionsSection(false);
            return;
        }

        var previous = _collections;
        _collections = row;
        CollectionsHeader.Text = row.Title;
        if (previous is not null && previous.IsPreview == row.IsPreview
            && previous.Cards.Select(card => card.ContentId).SequenceEqual(row.Cards.Select(card => card.ContentId), StringComparer.Ordinal))
        {
            // The same cards: refresh what RelatedTemplate binds one way (watched, episodes left), keeping the art it shows.
            var views = row.IsPreview ? _collectionTiles.OfType<CardView>().ToList() : _collectionCardViews;
            for (var index = 0; index < views.Count && index < row.Cards.Count; index++)
            {
                var shown = views[index].Card;
                var card = row.Cards[index];
                if (shown.Flags != card.Flags || shown.EpisodesLeft != card.EpisodesLeft) views[index].UpdateCard(card with { ImageUrl = shown.ImageUrl });
            }

            PlaceCollectionsSection(true);
            return;
        }

        var key = hadFocus ? FocusedCollectionsKey(focused) : null;
        _collectionTiles.Clear();
        _collectionCardViews = [];
        if (row.IsPreview)
        {
            // TvItemCollectionsRow.kt passes the preview's TvMediaRow no card actions.
            foreach (var card in row.Cards)
            {
                _collectionTiles.Add(new CardView(card, row.Title, CollectionCardWidth, CollectionCardHeight, landscape: false) { HasCardMenu = false });
            }

            _collectionTiles.Add(new CollectionExploreAllView());
            CollectionCards.ItemsSource = null;
        }
        else
        {
            _collectionCardViews = row.Cards.Select(card => new CardView(card, row.Title, CollectionCardWidth, CollectionCardHeight, landscape: false)).ToList();
            CollectionCards.ItemsSource = _collectionCardViews;
        }

        CollectionPreview.Visibility = row.IsPreview ? Visibility.Visible : Visibility.Collapsed;
        CollectionCards.Visibility = row.IsPreview ? Visibility.Collapsed : Visibility.Visible;
        _collectionPreviewRail.Remembered = null;
        _collectionCardsRail.Remembered = null;
        PlaceCollectionsSection(true);
        if (hadFocus)
        {
            var target = key is null ? null : CollectionsView(row, row.ReturnIndex(key));
            var generation = ++_focusGeneration;
            FocusRetry.Until(PageContent, () => !_active || generation != _focusGeneration
                || (target is not null ? CollectionsRail.FocusItem(target, FocusState.Keyboard) : CollectionsRail.Focus(FocusState.Keyboard)));
        }
    }

    /// <summary>The view at a <see cref="ItemCollectionsRow.ReturnIndex"/>: a title or Explore all, or an "Also in" card.</summary>
    private object? CollectionsView(ItemCollectionsRow row, int index) => index < 0 ? null
        : row.IsPreview ? index < _collectionTiles.Count ? _collectionTiles[index] : null
        : index < _collectionCardViews.Count ? _collectionCardViews[index] : null;

    private static string? FocusedCollectionsKey(DependencyObject? focused)
    {
        for (var node = focused; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ContentControl { Content: BrowseTileView tile }) return tile.Key;
        }

        return null;
    }

    /// <summary>
    /// TvItemDetailScreen.kt: below Cast &amp; Crew on a series page, below the box set row on a movie page (Bale builds no
    /// box set row, so after Trailers). The section is in the panel only while it has a row: StackPanel.Spacing also
    /// spaces collapsed children.
    /// </summary>
    private void PlaceCollectionsSection(bool shown)
    {
        var children = SupportingSections.Children;
        var index = children.IndexOf(CollectionsSection);
        _collectionsShown = shown;
        if (!shown)
        {
            if (index >= 0) children.RemoveAt(index);
            return;
        }

        var anchor = _detail?.IsSeries == true ? CastSection : TrailersSection;
        if (index >= 0 && index == children.IndexOf(anchor) + 1) return;
        if (index >= 0) children.RemoveAt(index);
        children.Insert(children.IndexOf(anchor) + 1, CollectionsSection);
    }

    private void OnCollectionClick(object sender, ItemClickEventArgs e)
    {
        try
        {
            OpenCollectionsItem(e.ClickedItem);
        }
        catch (Exception ex)
        {
            Log.Error("Detail open collection", ex);
        }
    }

    private bool IsCollectionsItem(object item) =>
        item is BrowseTileView tile && (_collectionTiles.Contains(tile) || (tile is CardView view && _collectionCardViews.Contains(view)));

    /// <summary>
    /// TvAppNavigation.kt onOpenCollection / onCollectionItem: Explore all and "Also in" cards open their collection; a
    /// preview title opens with no library, since a collection's titles can live in other libraries. Back lands on the
    /// card that opened the page (TvItemDetailScreen.kt recordReturn).
    /// </summary>
    private void OpenCollectionsItem(object item)
    {
        if (_collections is not { } row || Session.Client is not { } client) return;
        switch (item)
        {
            case CollectionExploreAllView when row.Collection is { } collection:
                OpenCollection(row, collection, ItemCollectionsRow.ExploreAllKey, client);
                break;
            case CardView { Card: { IsCollection: true } card }:
                OpenCollection(row, card, card.ContentId, client);
                break;
            case CardView { Card: var card }:
                RecordCollectionsReturn(card.ContentId);
                NavigateWithReturn(typeof(DetailPage), card.ContentId);
                break;
        }
    }

    private void OpenCollection(ItemCollectionsRow row, MediaCard collection, string key, SiloClient client)
    {
        RecordCollectionsReturn(key);
        NavigateWithReturn(typeof(CatalogGridPage), new CatalogGridEntry(row.Open(collection, client.LibraryBrowse), client));
    }

    private void RecordCollectionsReturn(string key)
    {
        _returnCastIndex = null;
        _returnSimilarId = null;
        _returnPlaybackFocus = null;
        _returnTrailerKey = null;
        _returnCollectionsKey = key;
    }
}
