using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Shapes;

namespace Bale.Xbox.Services;

/// <summary>Masked, unclipped elevation for Skyline surfaces; black by default, optionally a centered TV focus glow.</summary>
public sealed partial class SurfaceShadow : DependencyObject
{
    public static readonly DependencyProperty ElevationProperty = DependencyProperty.RegisterAttached(
        "Elevation", typeof(double), typeof(SurfaceShadow), new PropertyMetadata(0d, OnElevationChanged));

    public static double GetElevation(DependencyObject element) => (double)element.GetValue(ElevationProperty);
    public static void SetElevation(DependencyObject element, double value) => element.SetValue(ElevationProperty, value);

    // TvHeroActionPill.kt / TvProfileAccessControls.kt use white, centered elevation on focus.
    // Existing Marquee buttons/dialogs retain the same black, 50%, half-elevation-offset defaults.
    public static readonly DependencyProperty ColorProperty = DependencyProperty.RegisterAttached(
        "Color", typeof(Color), typeof(SurfaceShadow), new PropertyMetadata(Colors.Black));
    public static Color GetColor(DependencyObject element) => (Color)element.GetValue(ColorProperty);
    public static void SetColor(DependencyObject element, Color value) => element.SetValue(ColorProperty, value);

    public static readonly DependencyProperty OpacityProperty = DependencyProperty.RegisterAttached(
        "Opacity", typeof(double), typeof(SurfaceShadow), new PropertyMetadata(0.5d));
    public static double GetOpacity(DependencyObject element) => (double)element.GetValue(OpacityProperty);
    public static void SetOpacity(DependencyObject element, double value) => element.SetValue(OpacityProperty, value);

    public static readonly DependencyProperty OffsetScaleProperty = DependencyProperty.RegisterAttached(
        "OffsetScale", typeof(double), typeof(SurfaceShadow), new PropertyMetadata(0.5d));
    public static double GetOffsetScale(DependencyObject element) => (double)element.GetValue(OffsetScaleProperty);
    public static void SetOffsetScale(DependencyObject element, double value) => element.SetValue(OffsetScaleProperty, value);

    private static void OnElevationChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FrameworkElement element) return;
        element.Loaded -= OnLoaded;
        element.Loaded += OnLoaded;
    }

    private static void OnLoaded(object sender, RoutedEventArgs args)
    {
        try
        {
            if (sender is not FrameworkElement element || GetElevation(element) is not > 0) return;
            var root = element;
            if (element is Control)
            {
                if (VisualTreeHelper.GetChildrenCount(element) == 0
                    || VisualTreeHelper.GetChild(element, 0) is not FrameworkElement templateRoot) return;
                root = templateRoot;
            }
            if (root.FindName("ShadowHost") is not FrameworkElement host || root.FindName("ShadowMask") is not Shape mask
                || ElementCompositionPreview.GetElementChildVisual(host) is not null) return;

            // TvMarquee.kt and TvDialog.kt use Android elevation (12dp buttons, 30dp dialog).
            // UWP has no equivalent: approximate that elevation with a masked blur/offset.
            // Skyline.xaml also owns focus-glow color/opacity/offset; no resource lookup or implicit transition.
            // A shared template can serve pills of different heights (SkylineProfileSubmitStyle and
            // its 24dp SkylineProfileSmallActionStyle). The mask is drawn under the capsule, so a
            // tighter radius than the control's shows as square corners around a focused pill.
            if (element is Control { CornerRadius.TopLeft: var corner } && mask is Rectangle rectangle && rectangle.RadiusX < corner)
            {
                rectangle.RadiusX = corner;
                rectangle.RadiusY = corner;
            }

            var elevation = (float)GetElevation(element);
            UiShadow.Attach(host, mask, new UiShadow.Settings(GetColor(element),
                (float)Math.Clamp(GetOpacity(element), 0, 1), elevation * 2,
                elevation * (float)GetOffsetScale(element)));
        }
        catch (Exception ex)
        {
            Log.Error("Surface shadow", ex);
        }
    }
}
