using Bale.Core.Browse;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Bale.Xbox.Services;

/// <summary>Stand-in for the item detail page, which is the next milestone.</summary>
internal static class CardInfo
{
    public static async Task ShowAsync(MediaCard card)
    {
        var dialog = new ContentDialog
        {
            Title = card.Title,
            Content = new TextBlock
            {
                Text = string.Join("\n\n", new[] { card.MarqueeMeta, card.Overview, "The details page and playback are coming next." }
                    .Where(line => !string.IsNullOrEmpty(line))),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
            },
            CloseButtonText = "Close",
        };
        await dialog.ShowAsync();
    }
}
