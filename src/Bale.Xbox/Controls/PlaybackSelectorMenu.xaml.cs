using Bale.Core.Detail;
using Bale.Xbox.Services;
using Bale.Xbox.Views;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace Bale.Xbox.Controls;

/// <summary>TvAnchoredSelectorMenu.kt's modal, scrollable dropdown. All chrome lives in Skyline.xaml.</summary>
public sealed partial class PlaybackSelectorMenu : UserControl
{
    public static readonly DependencyProperty AnchorGapProperty = DependencyProperty.Register(
        nameof(AnchorGap), typeof(double), typeof(PlaybackSelectorMenu), new PropertyMetadata(0d));

    public static readonly DependencyProperty WindowInsetProperty = DependencyProperty.Register(
        nameof(WindowInset), typeof(double), typeof(PlaybackSelectorMenu), new PropertyMetadata(0d));

    private Button? _origin;
    private int _focusedIndex;
    private List<PlaybackOptionView> _options = [];

    public PlaybackSelectorMenu()
    {
        InitializeComponent();
        SizeChanged += (_, _) => PositionMenu();
        Loaded += (_, _) => UpdateOverflow();
    }

    public double AnchorGap { get => (double)GetValue(AnchorGapProperty); set => SetValue(AnchorGapProperty, value); }
    public double WindowInset { get => (double)GetValue(WindowInsetProperty); set => SetValue(WindowInsetProperty, value); }
    public bool IsOpen => ModalLayer.Visibility == Visibility.Visible;
    public event Action<PlaybackSelectorOption>? Chosen;
    public event Action? Dismissed;

    public void Show(string label, IReadOnlyList<PlaybackSelectorOption> options, Button origin)
    {
        _origin = origin;
        _options = options.Select((option, index) => new PlaybackOptionView(option, index)).ToList();
        _focusedIndex = PlaybackSelectorNavigation.InitialIndex(options);
        Header.Text = label.ToUpperInvariant();
        Options.ItemsSource = _options;
        ModalLayer.Visibility = Visibility.Visible;
        IsHitTestVisible = true;
        Scroller.ChangeView(null, 0, null, true);
        FocusRetry.Until(this, () => !IsOpen || (PositionMenu() && FocusRow(_focusedIndex, FocusState.Programmatic)));
    }

    public void Hide()
    {
        ModalLayer.Visibility = Visibility.Collapsed;
        IsHitTestVisible = false;
        _origin = null;
    }

    private bool PositionMenu()
    {
        if (!IsOpen || _origin is null || ActualWidth <= 0 || ActualHeight <= 0) return false;
        try
        {
            MenuPanel.UpdateLayout();
            var anchor = _origin.TransformToVisual(this).TransformBounds(new Rect(0, 0, _origin.ActualWidth, _origin.ActualHeight));
            var width = MenuPanel.ActualWidth;
            var height = MenuPanel.ActualHeight;
            var x = Math.Clamp(anchor.Left, 0, Math.Max(0, ActualWidth - width));
            var below = anchor.Bottom + AnchorGap;
            var above = anchor.Top - AnchorGap - height;
            // Material DropdownMenu tries below, then above, then the window's vertical margin.
            var y = below + height <= ActualHeight - WindowInset ? below
                : above >= WindowInset ? above : Math.Max(WindowInset, (ActualHeight - height) / 2);
            MenuPanel.Margin = new Thickness(x, y, 0, 0);
            UpdateOverflow();
            return width > 0 && height > 0;
        }
        catch (Exception ex)
        {
            Log.Error("Playback selector placement", ex);
            return false;
        }
    }

    private ToggleButton? Row(int index)
    {
        if (Options.ItemsPanelRoot is not { } panel) return null;
        foreach (var container in panel.Children)
        {
            if (FindChild<ToggleButton>(container) is { DataContext: PlaybackOptionView view } button && view.Index == index) return button;
        }

        return null;
    }

    private bool FocusRow(int index, FocusState state) => Row(index)?.Focus(state) == true;

    private void OnOptionFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PlaybackOptionView view } row) return;
        _focusedIndex = view.Index;
        // Own the scroll as well as the walk: below-fold options must be reachable, and only the
        // list scrolls. Header and panel remain anchored (TvAnchoredSelectorMenu.kt).
        var top = row.TransformToVisual(Options).TransformPoint(new Point(0, 0)).Y;
        var offset = PlaybackSelectorNavigation.ScrollTarget(Scroller.VerticalOffset, top, row.ActualHeight,
            Scroller.ViewportHeight, Scroller.ScrollableHeight);
        if (offset != Scroller.VerticalOffset) Scroller.ChangeView(null, offset, null);
    }

    private void OnOptionClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PlaybackOptionView view }) Chosen?.Invoke(view.Option);
    }

    private void OnMenuPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        try
        {
            if (e.Key is VirtualKey.Escape or VirtualKey.Back or VirtualKey.GamepadB)
            {
                Dismissed?.Invoke();
                e.Handled = true;
                return;
            }

            var step = e.Key switch
            {
                VirtualKey.Up or VirtualKey.GamepadDPadUp or VirtualKey.GamepadLeftThumbstickUp => -1,
                VirtualKey.Down or VirtualKey.GamepadDPadDown or VirtualKey.GamepadLeftThumbstickDown => 1,
                VirtualKey.Tab => (Window.Current.CoreWindow.GetKeyState(VirtualKey.Shift) & CoreVirtualKeyStates.Down) != 0 ? -1 : 1,
                _ => 0,
            };
            if (step != 0)
            {
                if (PlaybackSelectorNavigation.NextIndex(_options.Count, _focusedIndex, step > 0) is { } next) FocusRow(next, FocusState.Keyboard);
                e.Handled = true;
            }
            else if (e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.GamepadDPadLeft or VirtualKey.GamepadDPadRight
                or VirtualKey.GamepadLeftThumbstickLeft or VirtualKey.GamepadLeftThumbstickRight)
            {
                e.Handled = true;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Playback selector keys", ex);
        }
    }

    private void OnBackgroundTapped(object sender, TappedRoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, ModalLayer)) return;
        Dismissed?.Invoke();
        e.Handled = true;
    }

    private void OnPanelSizeChanged(object sender, SizeChangedEventArgs e) => PositionMenu();
    private void OnListSizeChanged(object sender, SizeChangedEventArgs e) => UpdateOverflow();
    private void OnScrollChanged(object sender, ScrollViewerViewChangedEventArgs e) => UpdateOverflow();

    private void UpdateOverflow()
    {
        // TvAnchoredSelectorMenu.kt: fade and chevron only where the list can still scroll.
        var top = Scroller.VerticalOffset > 0;
        var bottom = Scroller.VerticalOffset < Scroller.ScrollableHeight;
        TopOverflow.Visibility = top ? Visibility.Visible : Visibility.Collapsed;
        BottomOverflow.Visibility = bottom ? Visibility.Visible : Visibility.Collapsed;
        TopFade.Visibility = TopOverflow.Visibility;
        BottomFade.Visibility = BottomOverflow.Visibility;
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T self) return self;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            if (FindChild<T>(VisualTreeHelper.GetChild(parent, i)) is { } match) return match;
        }

        return null;
    }
}
