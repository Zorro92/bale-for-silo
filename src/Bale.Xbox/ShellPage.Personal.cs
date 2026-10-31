using Bale.Core;
using Bale.Core.Browse;
using Bale.Xbox.Services;
using Bale.Xbox.Views;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Input;

namespace Bale.Xbox;

public sealed partial class ShellPage
{
    private bool _personalReturnToProfile;
    private readonly Dictionary<string, (LibraryCatalogSource Source, SiloClient Client, string? ProfileId)> _personalSources = new();

    private LibraryCatalogSource PersonalSource(string kind, SiloClient client)
    {
        // TvPersonalListControlsViewModel: selections survive leaving/re-entering the same list,
        // independently per source and acting profile, without persisting library preferences.
        if (_personalSources.TryGetValue(kind, out var saved)
            && ReferenceEquals(saved.Client, client) && saved.ProfileId == client.ProfileId) return saved.Source;
        var source = LibraryCatalogSource.Personal(kind, client.LibraryBrowse);
        _personalSources[kind] = (source, client, client.ProfileId);
        return source;
    }

    /// <summary>
    /// TvMainShell.kt: profile/For You shortcuts push the same personal grids. Keep this entry point
    /// shared so neither dropdown can accidentally put a secondary screen inside the root frame.
    /// </summary>
    public static void OpenPersonalList(string kind)
    {
        if (Current is not { } shell || Session.Client is not { } client) return;
        ICatalogGridSource source = kind switch
        {
            "watchlist" or "favorites" => shell.PersonalSource(kind, client),
            "history" => new HistoryCatalogSource(),
            _ => throw new ArgumentException("Unknown personal list.", nameof(kind)),
        };
        shell._personalReturnToProfile = IsWithin(FocusManager.GetFocusedElement() as DependencyObject, shell.ProfilePanel);
        shell.ClosePanels();
        shell.Frame.Navigate(typeof(CatalogGridPage), new CatalogGridEntry(source, client));
    }
}
