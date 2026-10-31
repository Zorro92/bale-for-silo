using Bale.Core.Browse;
using Bale.Xbox.Services;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace Bale.Xbox.Controls;

/// <summary>TvLibraryBrowseControls.kt: scrolling capsule controls, not another navigation tab row.</summary>
public sealed partial class LibraryBrowseToolbar : UserControl
{
    private readonly Rail _rail;

    public LibraryBrowseToolbar()
    {
        InitializeComponent();
        _rail = Rail.Attach(Controls);
        _rail.Pin = false;
    }

    public event Action? SortRequested;
    public event Action? FilterRequested;
    public event Action? ClearRequested;
    public event Action? ContentRequested;
    public event Action? TopRequested;

    public void Update(LibraryBrowseFilter filter)
    {
        SortButton.Content = $"Sort · {filter.Sort.Label}   {filter.Sort.DirectionLabel(filter.Descending)}";
        FilterButton.Content = filter.Facets.ActiveCount > 0 ? $"Filter   {filter.Facets.ActiveCount}" : "Filter";
        ClearButton.Visibility = filter.Facets.CanReset ? Visibility.Visible : Visibility.Collapsed;
    }

    public bool FocusControls() => _rail.Focus();
    public void FocusSort() => SortButton.Focus(FocusState.Programmatic);
    public void FocusFilter() => FilterButton.Focus(FocusState.Programmatic);
    private void OnSortClick(object sender, RoutedEventArgs e) => SortRequested?.Invoke();
    private void OnFilterClick(object sender, RoutedEventArgs e) => FilterRequested?.Invoke();
    private void OnClearClick(object sender, RoutedEventArgs e) => ClearRequested?.Invoke();

    private void OnControlsKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // A collapsed optional item is still counted by ItemsControl; stop at the last visible pill.
        if (ClearButton.Visibility != Visibility.Visible && ReferenceEquals(FocusManager.GetFocusedElement(), FilterButton)
            && e.Key is VirtualKey.Right or VirtualKey.GamepadDPadRight or VirtualKey.GamepadLeftThumbstickRight)
        {
            e.Handled = true;
            return;
        }
        if (e.Key is VirtualKey.Up or VirtualKey.GamepadDPadUp or VirtualKey.GamepadLeftThumbstickUp)
        {
            e.Handled = true;
            TopRequested?.Invoke();
        }
        else if (e.Key is VirtualKey.Down or VirtualKey.GamepadDPadDown or VirtualKey.GamepadLeftThumbstickDown)
        {
            e.Handled = true;
            ContentRequested?.Invoke();
        }
    }
}
