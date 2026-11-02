using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;

namespace Bale.Xbox.Services;

internal static class SettingsInput
{
    public static bool IsBack(VirtualKey key) => key is VirtualKey.Escape or VirtualKey.Back or VirtualKey.GamepadB;

    public static int VerticalStep(VirtualKey key) => key switch
    {
        VirtualKey.Up or VirtualKey.GamepadDPadUp or VirtualKey.GamepadLeftThumbstickUp => -1,
        VirtualKey.Down or VirtualKey.GamepadDPadDown or VirtualKey.GamepadLeftThumbstickDown => 1,
        _ => 0,
    };

    public static int HorizontalStep(VirtualKey key) => key switch
    {
        VirtualKey.Left or VirtualKey.GamepadDPadLeft or VirtualKey.GamepadLeftThumbstickLeft => -1,
        VirtualKey.Right or VirtualKey.GamepadDPadRight or VirtualKey.GamepadLeftThumbstickRight => 1,
        _ => 0,
    };

    public static bool IsWithin(DependencyObject? element, DependencyObject ancestor)
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, ancestor)) return true;
        }
        return false;
    }
}
