using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Shapes;
using Path = Windows.UI.Xaml.Shapes.Path;

namespace Bale.Xbox.Services;

/// <summary>
/// The round actions beside Play (TvSquareToggleButton with titleOnFocus): at rest a 38 circle with
/// only its icon; focused, a white capsule showing its title. The button's content is a panel with
/// an icon and a TextBlock title. Templates can't reach into content, so the title and any path
/// icons are switched here; the template does the fill and the text color.
/// </summary>
internal static class ActionButton
{
    private static readonly SolidColorBrush Light = new(Colors.White);
    private static readonly SolidColorBrush Dark = new(Colors.Black);

    public static void Attach(Button button)
    {
        Show(button, false);
        button.GotFocus += (_, _) => Show(button, true);
        button.LostFocus += (_, _) => Show(button, false);
    }

    // UWP's StackPanel still adds Spacing after collapsed children, so a hidden label left the icon
    // off centre in its disc. Hidden labels take the spacing with them; focus restores it. The original
    // spacing is kept in the panel's Tag (on the element itself): a managed table keyed by the wrapper
    // lost it, because WinRT can hand back a new wrapper for the same element.
    internal static void SetLabelSpacing(Panel panel, bool labelsVisible)
    {
        if (panel is not StackPanel stack) return;
        if (stack.Tag is not double original)
        {
            original = stack.Spacing;
            stack.Tag = original;
        }
        stack.Spacing = labelsVisible ? original : 0;
    }

    private static void Show(Button button, bool focused)
    {
        if (button.Content is not Panel panel)
        {
            return;
        }

        SetLabelSpacing(panel, focused);

        VisualStateManager.GoToState(button, focused ? "Expanded" : "Collapsed", useTransitions: true);
        foreach (var child in panel.Children)
        {
            switch (child)
            {
                case TextBlock title:
                    title.Visibility = focused ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case Path path:
                    Recolor(path, focused);
                    break;
                case Viewbox { Child: Path iconPath }:
                    Recolor(iconPath, focused);
                    break;
                case Panel icon:
                    foreach (var part in icon.Children.OfType<Path>())
                    {
                        Recolor(part, focused);
                    }

                    break;
            }
        }
    }

    private static void Recolor(Path path, bool focused)
    {
        var brush = focused ? Dark : Light;
        if (path.Stroke is not null)
        {
            path.Stroke = brush;
        }

        if (path.Fill is not null)
        {
            path.Fill = brush;
        }
    }
}
