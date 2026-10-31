using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Bale.Xbox.Services;

/// <summary>
/// SkylineCardRailStyle's layout policy (TvMediaRow.kt's content padding). The ListView's
/// ItemsPresenter receives Padding INSIDE its ScrollViewer; outer margins only preserve row spacing.
/// No resource lookups, clipping overrides, focus handlers or scroll changes: safe under Native AOT.
/// </summary>
public sealed partial class RailLayout : DependencyObject
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(RailLayout), new PropertyMetadata(false, OnLayoutChanged));

    public static readonly DependencyProperty CardWidthProperty = DependencyProperty.RegisterAttached(
        "CardWidth", typeof(double), typeof(RailLayout), new PropertyMetadata(0d, OnLayoutChanged));

    public static readonly DependencyProperty CardHeightProperty = DependencyProperty.RegisterAttached(
        "CardHeight", typeof(double), typeof(RailLayout), new PropertyMetadata(0d, OnLayoutChanged));

    public static readonly DependencyProperty ContentInsetProperty = DependencyProperty.RegisterAttached(
        "ContentInset", typeof(Thickness), typeof(RailLayout), new PropertyMetadata(default(Thickness), OnLayoutChanged));

    public static readonly DependencyProperty FocusVisualProperty = DependencyProperty.RegisterAttached(
        "FocusVisual", typeof(RailFocusVisual), typeof(RailLayout), new PropertyMetadata(RailFocusVisual.Card, OnLayoutChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    public static double GetCardWidth(DependencyObject element) => (double)element.GetValue(CardWidthProperty);
    public static void SetCardWidth(DependencyObject element, double value) => element.SetValue(CardWidthProperty, value);

    public static double GetCardHeight(DependencyObject element) => (double)element.GetValue(CardHeightProperty);
    public static void SetCardHeight(DependencyObject element, double value) => element.SetValue(CardHeightProperty, value);

    /// <summary>Resting content insets from Skyline, not a page-specific focus-padding override.</summary>
    public static Thickness GetContentInset(DependencyObject element) => (Thickness)element.GetValue(ContentInsetProperty);
    public static void SetContentInset(DependencyObject element, Thickness value) => element.SetValue(ContentInsetProperty, value);

    public static RailFocusVisual GetFocusVisual(DependencyObject element) => (RailFocusVisual)element.GetValue(FocusVisualProperty);
    public static void SetFocusVisual(DependencyObject element, RailFocusVisual value) => element.SetValue(FocusVisualProperty, value);

    private static void OnLayoutChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not ListView list || !GetIsEnabled(list)
            || GetCardWidth(list) is not > 0 || GetCardHeight(list) is not > 0)
        {
            return;
        }

        var inset = GetContentInset(list);
        var (padding, margin) = CardRailMetrics.Layout(GetCardWidth(list), GetCardHeight(list),
            new RailInsets(inset.Left, inset.Top, inset.Right, inset.Bottom), GetFocusVisual(list));
        list.Padding = new Thickness(padding.Left, padding.Top, padding.Right, padding.Bottom);
        list.Margin = new Thickness(margin.Left, margin.Top, margin.Right, margin.Bottom);
    }
}
