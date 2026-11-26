using Bale.Core.Settings;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Bale.Xbox.Services;

/// <summary>
/// Scrolls a Settings list (the detail pane, a picker sheet) to its focused row as Android TV does
/// (<see cref="SettingsFocusScroll"/>). XAML brings a newly focused element into view only for keyboard focus, and then
/// just inside the edge; Settings moves focus itself, mostly with Focus(Programmatic), so each list reveals its row from
/// its focus handler, and <see cref="OwnBringIntoView"/> keeps XAML's reveal from moving the list a second time.
/// </summary>
internal static class SettingsScroll
{
    /// <summary>Stops XAML's bring-into-view at <paramref name="content"/>, the scroller's content, before the ScrollViewer sees it.</summary>
    public static void OwnBringIntoView(UIElement content) => content.BringIntoViewRequested += (_, args) => args.Handled = true;

    /// <summary>
    /// Scrolls <paramref name="scroller"/> so <paramref name="row"/>, inside its <paramref name="content"/>, settles at the
    /// pivot: a glide, or a jump when <paramref name="animate"/> is false. False while the row isn't in the list or either
    /// isn't measured yet.
    /// </summary>
    public static bool Reveal(ScrollViewer scroller, UIElement content, FrameworkElement row, bool animate)
    {
        try
        {
            if (!SettingsInput.IsWithin(row, content)) return false;
            var top = row.TransformToVisual(content).TransformPoint(new Point(0, 0)).Y;
            if (SettingsFocusScroll.Target(top, row.ActualHeight, scroller.ViewportHeight, scroller.ScrollableHeight) is not { } offset) return false;
            // Always issued, even onto the current offset: a newer focus must retarget a glide still running.
            scroller.ChangeView(null, offset, null, disableAnimation: !animate);
            return true;
        }
        catch (Exception error)
        {
            Log.Error("Settings scroll", error);
            return false;
        }
    }
}
