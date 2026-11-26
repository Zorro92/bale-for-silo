using Bale.Xbox.Services;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace Bale.Xbox.Controls;

/// <summary>
/// TvSettingsConfirmDialog (TvSettingsScreen.kt:2579): a title, a message, Cancel and one destructive action. SignOutDialog's
/// focus and key handling: Cancel takes focus, Back (on release) or an outside tap dismisses, focus never leaves.
/// </summary>
public sealed partial class SettingsConfirmDialog : UserControl
{
    private readonly Rail _actions;
    private UIElement? _focusRoot;
    private bool _backPressed;

    public SettingsConfirmDialog()
    {
        InitializeComponent();
        _actions = Rail.Attach(Actions);
        _actions.Pin = false;
        Unloaded += (_, _) => Hide();
    }

    public event Action? Confirmed;
    public event Action? Dismissed;
    public bool IsOpen => ModalLayer.Visibility == Visibility.Visible;

    public void Show(string title, string message, string confirmLabel)
    {
        if (IsOpen) return;
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmLabel;
        _backPressed = false;
        _actions.Remembered = CancelButton;
        _focusRoot = Window.Current.Content;
        if (_focusRoot is not null) _focusRoot.GettingFocus += OnRootGettingFocus;
        ModalLayer.Visibility = Visibility.Visible;
        IsHitTestVisible = true;
        // TvDialog.kt defaultTvDialogFocusIndex: the first Default action, never the destructive one.
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
            if (IsOpen && !IsWithin(args.NewFocusedElement) && !args.TrySetNewFocusedElement(CancelButton)) args.Cancel = true;
        }
        catch (Exception ex)
        {
            Log.Error("Settings confirm dialog focus", ex);
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
            // TvDialog.kt's BackHandler dismisses; wait for release so a held Back cannot also leave the page.
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
            var step = (Window.Current.CoreWindow.GetKeyState(VirtualKey.Shift) & CoreVirtualKeyStates.Down) != 0 ? -1 : 1;
            Control[] buttons = [CancelButton, ConfirmButton];
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
        if (ReferenceEquals(args.OriginalSource, ModalLayer))
        {
            args.Handled = true;
            Dismiss();
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs args) => Dismiss();

    private void OnConfirmClick(object sender, RoutedEventArgs args)
    {
        if (!IsOpen) return;
        Hide();
        Confirmed?.Invoke();
    }
}
