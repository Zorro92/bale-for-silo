namespace Bale.Core.Settings;

/// <summary>
/// Where a Settings list scrolls when one of its rows takes focus. TvSettingsScreen.kt's detail panes and
/// TvSettingsPickerSheet are LazyColumns that keep Compose's own focus scrolling, which on a TV (FEATURE_LEANBACK) is
/// PivotBringIntoViewSpec (androidx BringIntoViewSpec.android.kt, the default since Compose Foundation 1.7): the focused
/// row's top edge settles 30% of the way down the list, or its bottom on the list's end when it doesn't fit below that
/// line, within the list's scroll range. So a group's header stays on screen above its first row, and the rows near
/// either end leave the list where it is.
/// </summary>
public static class SettingsFocusScroll
{
    /// <summary>PivotBringIntoViewSpec.parentFraction: how far down the list's viewport a focused row's top settles.</summary>
    public const double PivotFraction = 0.3;

    /// <summary>
    /// The vertical offset that brings a focused row to the pivot: <paramref name="rowTop"/> is the row's top in the
    /// list's content (from its first pixel, so independent of the current offset), <paramref name="maxValue"/> the
    /// furthest the list scrolls. Null until the viewport and the row are measured.
    /// </summary>
    public static double? Target(double rowTop, double rowHeight, double viewport, double maxValue)
    {
        if (viewport <= 0 || rowHeight <= 0) return null;
        var pivot = PivotFraction * viewport;
        // childFraction 0: the row's top on the pivot, unless a row that fits the viewport would run past its end there.
        var leading = rowHeight <= viewport && viewport - pivot < rowHeight ? viewport - rowHeight : pivot;
        return Math.Clamp(rowTop - leading, 0, Math.Max(0, maxValue));
    }
}
