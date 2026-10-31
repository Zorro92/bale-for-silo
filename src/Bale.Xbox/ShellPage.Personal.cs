using Bale.Core.Browse;
using Bale.Xbox.Services;
using Bale.Xbox.Views;

namespace Bale.Xbox;

public sealed partial class ShellPage
{
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
        shell.ClosePanels();
        shell.Frame.Navigate(typeof(CatalogGridPage), new CatalogGridEntry(source, client));
    }
}
