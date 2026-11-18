using Bale.Core.Text;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Bale.Xbox.Controls;

/// <summary>
/// Compose's FlowRow for short action rows: children left to right, wrapping when the next one doesn't fit, so
/// no label is cut off (TvLoginScreen.kt CodeActions). Collapsed children take no room. Layout math is
/// <see cref="FlowLayout"/>.
/// </summary>
public sealed partial class FlowRow : Panel
{
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(FlowRow), new PropertyMetadata(0d, (d, _) => ((FlowRow)d).InvalidateMeasure()));

    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children) child.Measure(new Size(availableSize.Width, availableSize.Height));
        var (_, width, height) = Layout(availableSize.Width);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var visible = Visible();
        var (slots, _, _) = Layout(finalSize.Width);
        for (var i = 0; i < visible.Count; i++)
        {
            var size = visible[i].DesiredSize;
            visible[i].Arrange(new Rect(slots[i].X, slots[i].Y, size.Width, size.Height));
        }

        return finalSize;
    }

    /// <summary>The flow line each visible child sits on, for D-pad moves between lines.</summary>
    public int LineOf(UIElement child)
    {
        var visible = Visible();
        var index = visible.IndexOf(child);
        return index < 0 ? -1 : Layout(ActualWidth).Slots[index].Line;
    }

    public IReadOnlyList<UIElement> OnLine(int line)
    {
        var visible = Visible();
        var slots = Layout(ActualWidth).Slots;
        return visible.Where((_, i) => slots[i].Line == line).ToList();
    }

    private List<UIElement> Visible() => Children.Where(child => child.Visibility == Visibility.Visible).ToList();

    private (IReadOnlyList<FlowLayout.Slot> Slots, double Width, double Height) Layout(double available) =>
        FlowLayout.Arrange(Visible().Select(child => (child.DesiredSize.Width, child.DesiredSize.Height)).ToList(),
            double.IsInfinity(available) || available <= 0 ? double.PositiveInfinity : available, Spacing, Spacing);
}
