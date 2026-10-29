using Windows.UI.Xaml;

namespace Bale.Xbox.Services;

/// <summary>
/// Keeps trying a focus move on each layout pass until it succeeds. A page coming back from a
/// detail page (or loading) has its containers before they can take focus, so a single Focus()
/// call returns false and Windows' own first-focus pick wins.
/// </summary>
internal static class FocusRetry
{
    public static void Until(FrameworkElement layoutSource, Func<bool> tryFocus, int attempts = 60)
    {
        if (tryFocus())
        {
            return;
        }

        var left = attempts;
        void OnLayout(object? sender, object e)
        {
            if (tryFocus() || --left <= 0)
            {
                layoutSource.LayoutUpdated -= OnLayout;
            }
        }

        layoutSource.LayoutUpdated += OnLayout;
    }
}
