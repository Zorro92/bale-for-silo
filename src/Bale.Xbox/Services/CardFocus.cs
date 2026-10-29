using System.Numerics;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;

namespace Bale.Xbox.Services;

/// <summary>
/// Silo's Android TV card focus (siloCardDefaults): a 1.10 lift with a 2 px white ring and soft glow.
/// The ring lives in the card template as elements named FocusRing and FocusGlow.
/// </summary>
internal static class CardFocus
{
    public const float CardScale = 1.10f;

    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(120);

    public static void Apply(SelectorItem item, bool focused, float scale = CardScale)
    {
        item.ScaleTransition ??= new Vector3Transition { Duration = Duration };
        item.CenterPoint = new Vector3((float)item.ActualWidth / 2, (float)item.ActualHeight / 2, 0);
        var s = focused ? scale : 1f;
        item.Scale = new Vector3(s, s, 1);

        if (item.ContentTemplateRoot is FrameworkElement root)
        {
            SetOpacity(root, "FocusRing", focused ? 1 : 0);
            SetOpacity(root, "FocusGlow", focused ? 1 : 0);
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
