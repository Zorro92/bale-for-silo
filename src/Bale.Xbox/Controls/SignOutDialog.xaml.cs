using Bale.Xbox.Services;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace Bale.Xbox.Controls;

/// <summary>TvSignOutDialog.kt's keep/remove confirmation, shared by the picker and shell.</summary>
public sealed partial class SignOutDialog : UserControl
{
    private readonly Rail _actions;
    private UIElement? _focusRoot;
    private bool _backPressed;

    public SignOutDialog()
    {
        InitializeComponent();
        _actions = Rail.Attach(Actions);
        _actions.Pin = false;
        Unloaded += (_, _) => Hide();
    }

    public event Action<bool>? SignOutRequested;
    public event Action? Dismissed;
    public bool IsOpen => ModalLayer.Visibility == Visibility.Visible;

    public void Show()
    {
        if (IsOpen) return;
        _backPressed = false;
        _actions.Remembered = CancelButton;
        _focusRoot = Window.Current.Content;
        if (_focusRoot is not null) _focusRoot.GettingFocus += OnRootGettingFocus;
        ModalLayer.Visibility = Visibility.Visible;
        IsHitTestVisible = true;
        // TvDialog.kt defaultTvDialogFocusIndex: first Default action, not a destructive button.
        FocusRetry.Until(this, () => !IsOpen || (CancelButton.Focus(FocusState.Programmatic) && CancelButton.FocusState != FocusState.Unfocused));
    }

    public void Hide()
    {
        ModalLayer.Visibility = Visibility.Collapsed;
        IsHitTestVisible = false;
        _backPressed = false;
        if (_focusRoot is not null) _focusRoot.GettingFocus -= OnRootGettingFocus;
        _focusRoot = null;
    }

    public bool Dismiss()
    {
        if (!IsOpen) return false;
        Hide();
        Dismissed?.Invoke();
        return true;
    }

    private void OnRootGettingFocus(UIElement sender, GettingFocusEventArgs args)
    {
        try
        {
            if (IsOpen && !IsWithin(args.NewFocusedElement))
            {
                // TvDialog.kt uses a separate window plus tvModalFocusBoundary (TvModalFocusOwnership.kt).
                // This in-window UWP overlay must reject covered controls, even during initial focus retries.
                if (!args.TrySetNewFocusedElement(CancelButton)) args.Cancel = true;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Sign out dialog focus", ex);
        }
    }

    private bool IsWithin(DependencyObject? element)
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, this)) return true;
        }
        return false;
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is VirtualKey.Escape or VirtualKey.Back or VirtualKey.GamepadB)
        {
            // TvDialog.kt's BackHandler dismisses, never signs out. Wait for release so a held
            // Escape cannot dismiss this dialog and then leave the picker on the next repeat.
            _backPressed = true;
            args.Handled = true;
        }
        else if (args.Key is VirtualKey.Up or VirtualKey.Down
            or VirtualKey.GamepadDPadUp or VirtualKey.GamepadDPadDown
            or VirtualKey.GamepadLeftThumbstickUp or VirtualKey.GamepadLeftThumbstickDown)
        {
            args.Handled = true;
        }
        else if (args.Key == VirtualKey.Tab)
        {
            // TvModalFocusOwnership.kt cancels exit at the boundary; Tab must not reach the page either.
            var step = (Window.Current.CoreWindow.GetKeyState(VirtualKey.Shift) & CoreVirtualKeyStates.Down) != 0 ? -1 : 1;
            Control[] buttons = [CancelButton, SignOutButton, RemoveServerButton];
            var index = Array.FindIndex(buttons, button => button.FocusState != FocusState.Unfocused);
            buttons[Math.Clamp(index + step, 0, buttons.Length - 1)].Focus(FocusState.Keyboard);
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
        // TvDialog.kt also dismisses when its window's outside area is clicked.
        if (ReferenceEquals(args.OriginalSource, ModalLayer))
        {
            args.Handled = true;
            Dismiss();
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs args) => Dismiss();
    private void OnSignOutClick(object sender, RoutedEventArgs args) => Confirm(removeServer: false);
    private void OnRemoveServerClick(object sender, RoutedEventArgs args) => Confirm(removeServer: true);

    private void Confirm(bool removeServer)
    {
        if (!IsOpen) return;
        Hide();
        SignOutRequested?.Invoke(removeServer);
    }
}
