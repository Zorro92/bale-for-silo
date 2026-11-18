namespace Bale.Core.Text;

/// <summary>
/// Compose's FlowRow: items left to right with a fixed gap, wrapping onto a new line when the next one would pass
/// the available width (TvLoginScreen.kt CodeActions, Android 0548b9255 / Apple 538e2e9a: the declined sign-in
/// screen wraps its buttons instead of cutting a label off). An item wider than the line still gets its own line.
/// </summary>
public static class FlowLayout
{
    public readonly record struct Slot(double X, double Y, int Line);

    /// <returns>Each item's position and line, and the total extent.</returns>
    public static (IReadOnlyList<Slot> Slots, double Width, double Height) Arrange(
        IReadOnlyList<(double Width, double Height)> items, double available, double gap, double lineGap)
    {
        var slots = new List<Slot>(items.Count);
        double x = 0, y = 0, lineHeight = 0, width = 0;
        var line = 0;
        foreach (var (itemWidth, itemHeight) in items)
        {
            if (x > 0 && x + itemWidth > available)
            {
                y += lineHeight + lineGap;
                x = 0;
                lineHeight = 0;
                line++;
            }

            slots.Add(new Slot(x, y, line));
            width = Math.Max(width, x + itemWidth);
            lineHeight = Math.Max(lineHeight, itemHeight);
            x += itemWidth + gap;
        }

        return (slots, width, slots.Count == 0 ? 0 : y + lineHeight);
    }
}
