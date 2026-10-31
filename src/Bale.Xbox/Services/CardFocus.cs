using System.Numerics;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Bale.Xbox.Services;

/// <summary>
/// Card focus as Silo's TV apps draw it: the art lifts 1.05 with a 2 px white ring and soft glow,
/// while the caption underneath stays where it is. The card template names its art "Art" (holding
/// FocusRing and FocusGlow). Takes the container as a ContentControl: under Native AOT, focus
/// events report list containers as their ContentControl base, so `is ListViewItem` never matches.
/// </summary>
internal static class CardFocus
{
    public const float ArtScale = CardRailMetrics.ArtScale;

    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(120);

    public static void Apply(ContentControl item, bool focused, float artScale = ArtScale)
    {
        if (item.ContentTemplateRoot is not FrameworkElement root)
        {
            return;
        }

        SetOpacity(root, "FocusRing", focused ? 1 : 0);
        SetOpacity(root, "FocusGlow", focused ? 1 : 0);
        // CalendarEventCard brightens its caption but keeps it outside the focus lift.
        SetOpacity(root, "FocusCaption", focused ? 1 : 0);
        if (root.FindName("Art") is FrameworkElement art)
        {
            art.ScaleTransition ??= new Vector3Transition { Duration = Duration };
            art.CenterPoint = new Vector3((float)art.ActualWidth / 2, (float)art.ActualHeight / 2, 0);
            var scale = focused ? artScale : 1f;
            art.Scale = new Vector3(scale, scale, 1);
        }
    }

    private static void SetOpacity(FrameworkElement root, string name, double opacity)
    {
        if (root.FindName(name) is UIElement element)
        {
            element.Opacity = opacity;
        }
    }
}
