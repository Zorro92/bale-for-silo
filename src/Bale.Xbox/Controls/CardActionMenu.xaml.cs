using System.Numerics;
using Bale.Core.Browse;
using Bale.Xbox.Services;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Shapes;

namespace Bale.Xbox.Controls;

/// <summary>One native/AOT-safe card menu shared by every card surface; no runtime XAML or resource lookup.</summary>
public sealed partial class CardActionMenu : UserControl
{
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private ContentControl? _origin;
    private Control? _focusedRow;

    public CardActionMenu()
    {
        InitializeComponent();
        _statusTimer.Tick += (_, _) => ClearStatus();
        Unloaded += (_, _) => _statusTimer.Stop();
        SizeChanged += (_, _) => PositionMenu();
    }

    public event Action<CardAction>? Chosen;
    public event Action? Dismissed;
    public bool IsOpen => ModalLayer.Visibility == Visibility.Visible;
    internal Control? FocusTarget => _focusedRow is { Visibility: Visibility.Visible } ? _focusedRow : VisibleRows().FirstOrDefault();

    public void Show(IReadOnlyList<CardMenuEntry> entries, ContentControl origin)
    {
        ClearStatus();
        _origin = origin;
        _focusedRow = null;
        foreach (var button in Options.Children.OfType<Button>()) button.Visibility = Visibility.Collapsed;
        foreach (var entry in entries)
        {
            var (button, label) = entry.Action switch
            {
                CardAction.Play => (PlayItem, PlayLabel),
                CardAction.Watched => (WatchedItem, WatchedLabel),
                CardAction.Favorite => (FavoriteItem, FavoriteLabel),
                CardAction.Watchlist => (WatchlistItem, WatchlistLabel),
                CardAction.RemoveFromHome => (RemoveItem, RemoveLabel),
                _ => throw new ArgumentOutOfRangeException(nameof(entries)),
            };
            button.Visibility = Visibility.Visible;
            label.Text = entry.Label;
        }

        var icons = entries.Select(entry => entry.Icon).ToHashSet();
        CheckIcon.Visibility = Visible(icons.Contains(CardMenuIcon.Check));
        UnwatchedIcon.Visibility = Visible(icons.Contains(CardMenuIcon.VisibilityOff));
        FavoriteIcon.Visibility = Visible(icons.Contains(CardMenuIcon.Favorite));
        FavoriteBorderIcon.Visibility = Visible(icons.Contains(CardMenuIcon.FavoriteBorder));
        BookmarkAddIcon.Visibility = Visible(icons.Contains(CardMenuIcon.BookmarkAdd));
        BookmarkRemoveIcon.Visibility = Visible(icons.Contains(CardMenuIcon.BookmarkRemove));
        ModalLayer.Visibility = Visibility.Visible;
        IsHitTestVisible = true;
        FocusRetry.Until(this, () => !IsOpen || (PositionMenu() && VisibleRows().FirstOrDefault()?.Focus(FocusState.Programmatic) == true));
    }

    public void Hide()
    {
        ModalLayer.Visibility = Visibility.Collapsed;
        IsHitTestVisible = false;
        _origin = null;
        _focusedRow = null;
    }

    public void ShowStatus(string message)
    {
        StatusText.Text = message;
        StatusPanel.Visibility = Visibility.Visible;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    public void ClearStatus()
    {
        _statusTimer.Stop();
        StatusPanel.Visibility = Visibility.Collapsed;
    }

    public void HandleNavigation(KeyRoutedEventArgs args)
    {
        var step = args.Key switch
        {
            VirtualKey.Up or VirtualKey.GamepadDPadUp or VirtualKey.GamepadLeftThumbstickUp => -1,
            VirtualKey.Down or VirtualKey.GamepadDPadDown or VirtualKey.GamepadLeftThumbstickDown => 1,
            VirtualKey.Tab => (Window.Current.CoreWindow.GetKeyState(VirtualKey.Shift) & CoreVirtualKeyStates.Down) != 0 ? -1 : 1,
            _ => 0,
        };
        if (step != 0)
        {
            var rows = VisibleRows().ToList();
            var index = rows.FindIndex(row => ReferenceEquals(row, FocusManager.GetFocusedElement()));
            if (rows.Count > 0) rows[Math.Clamp(index + step, 0, rows.Count - 1)].Focus(FocusState.Keyboard);
            args.Handled = true;
        }
        else if (args.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.GamepadDPadLeft or VirtualKey.GamepadDPadRight
            or VirtualKey.GamepadLeftThumbstickLeft or VirtualKey.GamepadLeftThumbstickRight)
        {
            // TvMediaCardContextMenu captures focus: a direction never leaks to an underlying rail.
            args.Handled = true;
        }
    }

    private IEnumerable<Button> VisibleRows() => Options.Children.OfType<Button>().Where(row => row.Visibility == Visibility.Visible);

    private bool PositionMenu()
    {
        if (!IsOpen || _origin is null || ActualWidth <= 0 || ActualHeight <= 0) return false;
        try
        {
            MenuPanel.UpdateLayout();
            // TvMediaCardContextMenu precedes the art in a zero-height Box. Use its unscaled layout
            // slot, including grid headroom, not the zoomed art bounds or the container's caption.
            var anchor = _origin.TransformToVisual(this).TransformBounds(new Rect(0, 0, _origin.ActualWidth, 0));
            if (_origin.ContentTemplateRoot is FrameworkElement root && root.FindName("Art") is FrameworkElement art
                && VisualTreeHelper.GetParent(art) is UIElement parent)
            {
                var slot = LayoutInformation.GetLayoutSlot(art);
                anchor = parent.TransformToVisual(this).TransformBounds(new Rect(slot.X + art.Margin.Left,
                    slot.Y + art.Margin.Top, art.ActualWidth, 0));
            }

            var (x, y) = CardMenuPlacement.Position(anchor.Left, anchor.Top, anchor.Width, ActualWidth, ActualHeight,
                MenuPanel.ActualWidth, MenuPanel.ActualHeight);
            MenuPanel.Margin = new Thickness(x, y, 0, 0);
            return true;
        }
        catch (Exception ex)
        {
            // A quiet feed refresh can detach the anchor while the popup is being laid out.
            Log.Error("Card menu position", ex);
            Dismissed?.Invoke();
            return false;
        }
    }

    private static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    private void OnBackgroundTapped(object sender, TappedRoutedEventArgs args)
    {
        if (ReferenceEquals(args.OriginalSource, ModalLayer))
        {
            args.Handled = true;
            Dismissed?.Invoke();
        }
    }

    private void OnRowFocus(object sender, RoutedEventArgs args)
    {
        if (sender is not ContentControl row) return;
        _focusedRow = row;
        Canvas.SetZIndex(row, 1);
        EnsureRowGlow(row);
        // Only recolor the content icons, never the template's white glow mask.
        RecolorIcons(row.Content as DependencyObject, true);
    }

    private void OnRowBlur(object sender, RoutedEventArgs args)
    {
        if (sender is not ContentControl row) return;
        Canvas.SetZIndex(row, 0);
        RecolorIcons(row.Content as DependencyObject, false);
    }

    private static void EnsureRowGlow(ContentControl row)
    {
        if (VisualTreeHelper.GetChildrenCount(row) == 0
            || VisualTreeHelper.GetChild(row, 0) is not FrameworkElement root
            || root.FindName("GlowHost") is not FrameworkElement host
            || root.FindName("GlowMask") is not Shape mask
            || ElementCompositionPreview.GetElementChildVisual(host) is not null) return;

        // TvMenuRow / SurfaceGlow.kt: a rounded, zero-offset white shadow at 14%, with 10dp blur.
        var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
        var shadow = compositor.CreateDropShadow();
        shadow.Color = Colors.White;
        shadow.Opacity = 0.14f;
        shadow.BlurRadius = 10;
        shadow.Mask = mask.GetAlphaMask();
        var glow = compositor.CreateSpriteVisual();
        glow.RelativeSizeAdjustment = Vector2.One;
        glow.Shadow = shadow;
        ElementCompositionPreview.SetElementChildVisual(host, glow);
    }

    private static void RecolorIcons(DependencyObject? node, bool focused)
    {
        if (node is null) return;
        if (node is Shape { Fill: not null } shape)
        {
            shape.Fill = new SolidColorBrush(focused ? Colors.Black : Colors.White);
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) RecolorIcons(VisualTreeHelper.GetChild(node, i), focused);
    }

    private void OnPlayClick(object sender, RoutedEventArgs args) => Chosen?.Invoke(CardAction.Play);
    private void OnWatchedClick(object sender, RoutedEventArgs args) => Chosen?.Invoke(CardAction.Watched);
    private void OnFavoriteClick(object sender, RoutedEventArgs args) => Chosen?.Invoke(CardAction.Favorite);
    private void OnWatchlistClick(object sender, RoutedEventArgs args) => Chosen?.Invoke(CardAction.Watchlist);
    private void OnRemoveClick(object sender, RoutedEventArgs args) => Chosen?.Invoke(CardAction.RemoveFromHome);
}
