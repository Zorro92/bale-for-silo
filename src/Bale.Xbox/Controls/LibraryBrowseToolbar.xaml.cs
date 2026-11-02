using Bale.Core.Browse;
using Bale.Xbox.Services;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
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
        SortLabel.Text = $"Sort · {filter.Sort.Label}";
        SortDirection.Text = filter.Sort.DirectionLabel(filter.Descending);
        FilterCount.Text = filter.Facets.ActiveCount.ToString();
        // TvLibraryBrowseControls.kt counts active dimensions, not individual selected facet values.
        VisualStateManager.GoToState(this, filter.Facets.ActiveCount > 0 ? "FilterActive" : "FilterInactive", false);
        ClearButton.Visibility = filter.Facets.ActiveCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(SortButton, $"{SortLabel.Text}, {SortDirection.Text}");
        AutomationProperties.SetName(FilterButton, filter.Facets.ActiveCount > 0 ? $"Filter, {filter.Facets.ActiveCount} active" : "Filter");
    }

    public void UseCollectionHeader() => VisualStateManager.GoToState(this, "CollectionHeader", false);

    public void UpdateCount(long? total)
    {
        // TvPersonalScreens.PersonalControlHeader hides the count while a query is being replaced.
        ItemCount.Text = total == 1 ? "1 item" : $"{total} items";
        ItemCount.Visibility = total > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public Control FilterFocusTarget => FilterButton;
    public bool FocusControls() => _rail.Focus();
    public bool FocusSort() => FocusPill(SortButton);
    public void FocusFilter() => FocusPill(FilterButton);

    // Coming from outside the toolbar (the facet panel, a reload), Rail entry redirects to its
    // remembered pill, which after Clear is the collapsed Clear pill and falls back to Sort.
    // TvLibraryBrowseControls.kt claims the Filter pill explicitly, so name it as the entry first.
    /// <summary>Focuses one of this toolbar's pills as its Rail entry; null when the control isn't one.</summary>
    public bool? FocusPill(Control control) => control is Button pill && (pill == SortButton || pill == FilterButton || pill == ClearButton)
        ? (pill.Visibility == Visibility.Visible ? FocusPill(pill) : FocusPill(FilterButton))
        : null;

    private bool FocusPill(Button pill)
    {
        _rail.Remembered = pill.Content;
        return pill.Focus(FocusState.Programmatic);
    }
    private void OnSortClick(object sender, RoutedEventArgs e) => SortRequested?.Invoke();
    private void OnFilterClick(object sender, RoutedEventArgs e) => FilterRequested?.Invoke();
    private void OnClearClick(object sender, RoutedEventArgs e) => ClearRequested?.Invoke();
    private void OnPillFocus(object sender, RoutedEventArgs e) => SetPillFocus(sender, true);
    private void OnPillBlur(object sender, RoutedEventArgs e) => SetPillFocus(sender, false);

    private void SetPillFocus(object sender, bool focused)
    {
        var prefix = sender is FrameworkElement { Name: "SortButton" } ? "Sort"
            : sender is FrameworkElement { Name: "FilterButton" } ? "Filter" : "Clear";
        VisualStateManager.GoToState(this, prefix + (focused ? "Focused" : "Resting"), false);
    }

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
