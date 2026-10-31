// Only the events and tree traversal used by FocusRetry. These tests exercise its coordination,
// not UWP layout, actual focus movement, XAML/Composition integration or Native AOT behavior.
namespace Windows.UI.Xaml
{
    internal class DependencyObject
    {
        public DependencyObject? Parent { get; init; }
    }

    internal class RoutedEventArgs : EventArgs;
    internal delegate void RoutedEventHandler(object sender, RoutedEventArgs args);

    internal class FrameworkElement : DependencyObject
    {
        public event EventHandler<object>? LayoutUpdated;
        public event RoutedEventHandler? Unloaded;

        public void Layout() => LayoutUpdated?.Invoke(this, EventArgs.Empty);
        public void Unload() => Unloaded?.Invoke(this, new RoutedEventArgs());
    }
}

namespace Windows.UI.Xaml.Media
{
    internal static class VisualTreeHelper
    {
        public static DependencyObject? GetParent(DependencyObject element) => element.Parent;
    }
}
