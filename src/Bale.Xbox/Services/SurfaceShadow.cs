using System.Numerics;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Shapes;

namespace Bale.Xbox.Services;

/// <summary>Masked, unclipped black elevation for Skyline button and dialog surfaces.</summary>
public sealed partial class SurfaceShadow : DependencyObject
{
    public static readonly DependencyProperty ElevationProperty = DependencyProperty.RegisterAttached(
        "Elevation", typeof(double), typeof(SurfaceShadow), new PropertyMetadata(0d, OnElevationChanged));

    public static double GetElevation(DependencyObject element) => (double)element.GetValue(ElevationProperty);
    public static void SetElevation(DependencyObject element, double value) => element.SetValue(ElevationProperty, value);

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
            // UWP has no equivalent: approximate that elevation with a masked black blur/offset.
            // Skyline.xaml owns the elevation; no resource lookup or implicit template transition.
            var elevation = (float)GetElevation(element);
            var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
            var shadow = compositor.CreateDropShadow();
            shadow.Color = Colors.Black;
            shadow.Opacity = 0.5f;
            shadow.BlurRadius = elevation * 2;
            shadow.Offset = new Vector3(0, elevation / 2, 0);
            shadow.Mask = mask.GetAlphaMask();
            var visual = compositor.CreateSpriteVisual();
            visual.RelativeSizeAdjustment = Vector2.One;
            visual.Shadow = shadow;
            ElementCompositionPreview.SetElementChildVisual(host, visual);
        }
        catch (Exception ex)
        {
            Log.Error("Surface shadow", ex);
        }
    }
}
