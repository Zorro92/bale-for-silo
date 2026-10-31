using Bale.Core.Browse;
using Bale.Xbox.Services;
using Bale.Xbox.Views;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Input;

namespace Bale.Xbox;

public sealed partial class ShellPage
{
    private bool _personalReturnToProfile;

    /// <summary>
    /// TvMainShell.kt: profile/For You shortcuts push the same personal grids. Keep this entry point
    /// shared so neither dropdown can accidentally put a secondary screen inside the root frame.
    /// </summary>
    public static void OpenPersonalList(string kind)
    {
        if (Current is not { } shell || Session.Client is not { } client) return;
        ICatalogGridSource source = kind switch
        {
            "watchlist" or "favorites" => LibraryCatalogSource.Personal(kind, client.LibraryBrowse),
            "history" => new HistoryCatalogSource(),
            _ => throw new ArgumentException("Unknown personal list.", nameof(kind)),
        };
        shell._personalReturnToProfile = IsWithin(FocusManager.GetFocusedElement() as DependencyObject, shell.ProfilePanel);
        shell.ClosePanels();
        shell.Frame.Navigate(typeof(CatalogGridPage), new CatalogGridEntry(source, client));
    }
}
