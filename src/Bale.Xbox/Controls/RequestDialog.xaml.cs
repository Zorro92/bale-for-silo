using Bale.Xbox.Services;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace Bale.Xbox.Controls;

/// <summary>TvDialog.kt: focus ownership is modal, with Close as the safe initial action.</summary>
public sealed partial class RequestDialog : UserControl
{
    private readonly Rail _actions;
    private UIElement? _focusRoot;
    private bool _backPressed;

    public RequestDialog()
    {
        InitializeComponent();
        _actions = Rail.Attach(Actions);
        _actions.Pin = false;
        Unloaded += (_, _) => Hide();
    }

    public bool IsOpen => ModalLayer.Visibility == Visibility.Visible;
    public event Action? Confirmed;
    public event Action? Dismissed;

    public void Show(string title, string message, bool canCancel = false)
    {
        Hide();
        TitleText.Text = title;
        MessageText.Text = message;
        CancelButton.Visibility = canCancel ? Visibility.Visible : Visibility.Collapsed;
        _actions.Remembered = CloseButton;
        _focusRoot = Window.Current.Content;
        if (_focusRoot is not null) _focusRoot.GettingFocus += OnGettingFocus;
        ModalLayer.Visibility = Visibility.Visible;
        IsHitTestVisible = true;
        FocusRetry.Until(this, () => !IsOpen || CloseButton.Focus(FocusState.Programmatic));
    }

    public void Hide()
    {
        ModalLayer.Visibility = Visibility.Collapsed;
        IsHitTestVisible = false;
        _backPressed = false;
        if (_focusRoot is not null) _focusRoot.GettingFocus -= OnGettingFocus;
        _focusRoot = null;
    }

    public bool Dismiss()
    {
        if (!IsOpen) return false;
        Hide();
        Dismissed?.Invoke();
        return true;
    }

    private void OnGettingFocus(UIElement sender, GettingFocusEventArgs args)
    {
        try
        {
            if (!IsOpen) return;
            for (var node = args.NewFocusedElement; node is not null; node = VisualTreeHelper.GetParent(node))
                if (ReferenceEquals(node, this)) return;
            if (!args.TrySetNewFocusedElement(CloseButton)) args.Cancel = true;
        }
        catch (Exception ex) { Log.Error("Request dialog focus", ex); }
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is VirtualKey.Escape or VirtualKey.Back or VirtualKey.GamepadB)
        {
            args.Handled = true;
            _backPressed = true;
        }
        else if (args.Key is VirtualKey.Up or VirtualKey.Down or VirtualKey.Tab
            or VirtualKey.GamepadDPadUp or VirtualKey.GamepadDPadDown
            or VirtualKey.GamepadLeftThumbstickUp or VirtualKey.GamepadLeftThumbstickDown)
        {
            // Tab and vertical keys cannot escape the two-action rail.
            args.Handled = true;
        }
    }

    private void OnPreviewKeyUp(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is not (VirtualKey.Escape or VirtualKey.Back or VirtualKey.GamepadB)) return;
        args.Handled = true;
        if (_backPressed) Dismiss();
    }

    private void OnBackgroundTapped(object sender, TappedRoutedEventArgs args)
    {
        if (!ReferenceEquals(args.OriginalSource, ModalLayer)) return;
        args.Handled = true;
        Dismiss();
    }

    private void OnCloseClick(object sender, RoutedEventArgs args) => Dismiss();
    private void OnCancelClick(object sender, RoutedEventArgs args)
    {
        if (!IsOpen || CancelButton.Visibility != Visibility.Visible) return;
        Hide();
        Confirmed?.Invoke();
    }
}
