using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace Bale.Xbox.Services;

/// <summary>
/// How every horizontal rail behaves on Silo's TV apps (TvMediaRow, TvSeasonPicker,
/// TvDetailEpisodeRail, TvCastCrewSection), in one place so each page doesn't re-solve it:
/// <list type="bullet">
/// <item>Left/Right never leave the rail. At either end nothing happens (the ListView would
/// otherwise hand focus to whatever sits nearest on screen, in another row).</item>
/// <item>Focus entering the rail from outside lands on the item it should (the remembered card, or
/// the selected season chip) instead of the one nearest on screen (Compose's focusRestorer and
/// <c>focusProperties { enter = … }</c>).</item>
/// <item>With <see cref="Pin"/>, the focused card slides to the rail's start padding
/// (tvRailPinOnFocus); with <see cref="Center"/>, it stays in the middle; otherwise the rail scrolls just enough to show it.</item>
/// <item>The rail's own bring-into-view stops at the rail, so Left/Right never scroll the page
/// vertically. Pages place rails vertically themselves (row pinning, section anchors) from
/// <see cref="Entered"/>.</item>
/// </list>
/// Works with any ItemsControl: containers are found among <c>ItemsPanelRoot.Children</c> (under
/// Native AOT <c>ContainerFromIndex</c> returns nothing for these lists).
/// </summary>
internal sealed class Rail
{
    private readonly ItemsControl _list;
    private readonly RailScrollState _scrollState = new();
    private ScrollViewer? _scroller;
    private bool _hasFocus;
    private bool _preserveHorizontalOffset;

    private Rail(ItemsControl list)
    {
        _list = list;
        list.PreviewKeyDown += OnPreviewKeyDown;
        list.GettingFocus += OnGettingFocus;
        list.GotFocus += OnGotFocus;
        list.LostFocus += OnLostFocus;
        list.BringIntoViewRequested += (_, args) => args.Handled = true;
        list.Loaded += (_, _) => { Scroller(); };
        list.Unloaded += (_, _) => { ReleaseScroller(); _hasFocus = false; };
    }

    public static Rail Attach(ItemsControl list) => new(list);

    /// <summary>Slide the focused item to the rail's start padding (cards), or only reveal it (chips).</summary>
    public bool Pin { get; set; } = true;

    /// <summary>With <see cref="Pin"/> off, keep the focused item in the middle of the rail (TvSeasonPicker); the ends stay clamped.</summary>
    public bool Center { get; set; }

    /// <summary>The item focus last sat on; where focus returns when it comes back to the rail.</summary>
    public object? Remembered { get; set; }

    /// <summary>Overrides <see cref="Remembered"/> on entry, e.g. the selected season.</summary>
    public Func<object?>? EntryItem { get; set; }

    /// <summary>An item took focus: its container and its data.</summary>
    public event Action<Control, object>? ItemFocused;

    /// <summary>Focus came into the rail from outside it.</summary>
    public event Action? Entered;

    /// <summary>Left (-1) or Right (+1) was pressed at that end of the rail; focus stays put.</summary>
    public event Action<int>? EdgeReached;

    public ItemsControl List => _list;

    /// <summary>
    /// Focus the entry item. Vertical moves can keep the row's offset instead of re-pinning its
    /// remembered card; ordinary entry and Left/Right retain the shared positioning behavior.
    /// </summary>
    public bool Focus(FocusState state = FocusState.Programmatic, bool preserveHorizontalOffset = false)
    {
        // A virtualized entry item may need another layout. Do not replace a remembered vertical
        // target with whichever container happens to be realized while the row is being revealed.
        if (preserveHorizontalOffset && (EntryItem?.Invoke() ?? Remembered) is { } item)
        {
            return FocusItem(item, state, preserveHorizontalOffset: true);
        }

        return FocusControl(Target() ?? _list, state, preserveHorizontalOffset);
    }

    /// <summary>Focus <paramref name="item"/>, when its container exists.</summary>
    public bool FocusItem(object item, FocusState state = FocusState.Programmatic, bool preserveHorizontalOffset = false) =>
        FocusableIn(ContainerFor(item)) is { } target && FocusControl(target, state, preserveHorizontalOffset);

    /// <summary>
    /// Explicit first-card entry resets the offset without a sideways animation. Do not fall back
    /// to the first realized container: a scrolled/virtualized rail may not have card zero yet.
    /// Call through FocusRetry, just like other entry moves.
    /// </summary>
    public bool FocusFirst(FocusState state = FocusState.Programmatic)
    {
        if (_list.Items.Count == 0) return false;
        var first = _list.Items[0];
        Remembered = first;
        if (Scroller() is { } scroller && (scroller.HorizontalOffset != 0 || _scrollState.IsAnimating))
        {
            scroller.ChangeView(0, null, null, disableAnimation: true);
        }

        var entry = EntryItem;
        EntryItem = () => first;
        try
        {
            return FocusItem(first, state, preserveHorizontalOffset: true);
        }
        finally
        {
            EntryItem = entry;
        }
    }

    private bool FocusControl(Control target, FocusState state, bool preserveHorizontalOffset)
    {
        var previous = _preserveHorizontalOffset;
        _preserveHorizontalOffset |= preserveHorizontalOffset;
        try
        {
            return target.Focus(state);
        }
        finally
        {
            _preserveHorizontalOffset = previous;
        }
    }

    private Control? Target()
    {
        var item = EntryItem?.Invoke() ?? Remembered;
        return FocusableIn(item is null ? null : ContainerFor(item)) ?? FocusableIn(Containers().FirstOrDefault());
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var step = e.Key switch
        {
            VirtualKey.Left or VirtualKey.GamepadDPadLeft or VirtualKey.GamepadLeftThumbstickLeft => -1,
            VirtualKey.Right or VirtualKey.GamepadDPadRight or VirtualKey.GamepadLeftThumbstickRight => 1,
            _ => 0,
        };
        if (step == 0 || ContainerOf(e.OriginalSource as DependencyObject) is not { } container)
        {
            return;
        }

        // Layout.kt / tvRailPinOnFocus gates only horizontal input. WasKeyDown keeps a new
        // press responsive, including on entry from another row; never delay or replay a repeat.
        if (_scrollState.ShouldDrop(step, e.KeyStatus.WasKeyDown))
        {
            e.Handled = true;
            return;
        }

        var index = _list.IndexFromContainer(container);
        if ((step < 0 && index == 0) || (step > 0 && index == _list.Items.Count - 1))
        {
            e.Handled = true;
            EdgeReached?.Invoke(step);
        }
    }

    private void OnGettingFocus(UIElement sender, GettingFocusEventArgs args)
    {
        // Runs inside the focus engine: nothing here may throw.
        try
        {
            if (IsInside(args.OldFocusedElement) || !IsInside(args.NewFocusedElement)
                || Target() is not { } target || ReferenceEquals(target, args.NewFocusedElement))
            {
                return;
            }

            args.TrySetNewFocusedElement(target);
        }
        catch (Exception ex)
        {
            Log.Error("Rail entry focus", ex);
        }
    }

    private void OnGotFocus(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ContainerOf(e.OriginalSource as DependencyObject) is not { } container || ItemOf(container) is not { } item)
            {
                return;
            }

            var entered = !_hasFocus;
            _hasFocus = true;
            Remembered = item;
            if (!_preserveHorizontalOffset && container is FrameworkElement element)
            {
                if (Pin) PinToStart(element);
                else if (Center) CenterOn(element);
            }

            ItemFocused?.Invoke(e.OriginalSource as Control ?? (Control)container, item);
            if (entered)
            {
                Entered?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Rail focus", ex);
        }
    }

    private void OnLostFocus(object sender, RoutedEventArgs e)
    {
        // LostFocus also fires when focus moves between items; only leaving the rail counts.
        _hasFocus = IsInside(FocusManager.GetFocusedElement() as DependencyObject);
    }

    private void PinToStart(FrameworkElement container)
    {
        if (Scroller() is not { } scroller)
        {
            return;
        }

        var x = container.TransformToVisual(scroller).TransformPoint(new Windows.Foundation.Point(0, 0)).X;
        var padding = _list is Control control ? control.Padding.Left : 0;
        ScrollTo(scroller, scroller.HorizontalOffset + x - padding);
    }

    private void CenterOn(FrameworkElement container)
    {
        if (Scroller() is not { } scroller)
        {
            return;
        }

        var x = container.TransformToVisual(scroller).TransformPoint(new Windows.Foundation.Point(0, 0)).X;
        ScrollTo(scroller, scroller.HorizontalOffset + x + container.ActualWidth / 2 - scroller.ViewportWidth / 2);
    }

    private void ScrollTo(ScrollViewer scroller, double offset)
    {
        if (_scrollState.Begin(scroller.HorizontalOffset, offset, scroller.ScrollableWidth) is not { } target) return;
        if (!scroller.ChangeView(target, null, null)) _scrollState.Reset();
    }

    private ScrollViewer? Scroller()
    {
        var scroller = FindChild<ScrollViewer>(_list);
        if (ReferenceEquals(scroller, _scroller)) return _scroller;
        ReleaseScroller();
        _scroller = scroller;
        if (scroller is not null)
        {
            scroller.ViewChanging += OnViewChanging;
            scroller.ViewChanged += OnViewChanged;
        }
        return scroller;
    }

    private void OnViewChanging(object sender, ScrollViewerViewChangingEventArgs e)
    {
        if (sender is ScrollViewer scroller) _scrollState.ViewChanging(scroller.HorizontalOffset, e.FinalView.HorizontalOffset);
    }

    private void OnViewChanged(object sender, ScrollViewerViewChangedEventArgs e) => _scrollState.ViewChanged(e.IsIntermediate);

    private void ReleaseScroller()
    {
        if (_scroller is { } scroller)
        {
            scroller.ViewChanging -= OnViewChanging;
            scroller.ViewChanged -= OnViewChanged;
        }
        _scroller = null;
        _scrollState.Reset();
    }

    private IEnumerable<DependencyObject> Containers() =>
        _list.ItemsPanelRoot?.Children.Cast<DependencyObject>() ?? [];

    private DependencyObject? ContainerFor(object item) =>
        Containers().FirstOrDefault(container => ReferenceEquals(ItemOf(container), item));

    private static object? ItemOf(DependencyObject container) => container switch
    {
        ContentControl control => control.Content,
        ContentPresenter presenter => presenter.Content,
        FrameworkElement element => element.DataContext,
        _ => null,
    };

    /// <summary>The container itself when it takes focus (ListViewItem), else its first focusable child (a chip's button).</summary>
    private static Control? FocusableIn(DependencyObject? container) => container switch
    {
        null => null,
        Control { IsTabStop: true } control => control,
        _ => FocusManager.FindFirstFocusableElement(container) as Control,
    };

    /// <summary>The direct child of the items panel that holds <paramref name="element"/>.</summary>
    private DependencyObject? ContainerOf(DependencyObject? element)
    {
        var panel = _list.ItemsPanelRoot;
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (panel is not null && ReferenceEquals(VisualTreeHelper.GetParent(node), panel))
            {
                return node;
            }
        }

        return null;
    }

    private bool IsInside(DependencyObject? element)
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, _list))
            {
                return true;
            }
        }

        return false;
    }

    internal static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            if (FindChild<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}
