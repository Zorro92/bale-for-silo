using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Bale.Xbox.Services;

/// <summary>The existing Detail Play path, also used by Continue Watching's Play/Resume card action.</summary>
internal static class Playback
{
    public static async Task ShowComingAsync()
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = "Playback is next",
                Content = new TextBlock { Text = "Playing titles arrives in the next milestone.", TextWrapping = TextWrapping.Wrap, FontSize = 12 },
                CloseButtonText = "OK",
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Playback notice", ex);
        }
    }
}
