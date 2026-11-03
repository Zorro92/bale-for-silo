using Bale.Xbox.Services;
using Windows.System;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace Bale.Xbox.Controls;

/// <summary>TvTextInputDialog.kt: explicit selection opens the IME, closing restores the field surface.</summary>
public sealed partial class ProfileTextDialog : UserControl
{
    private readonly Rail _actions;
    private UIElement? _focusRoot;
    private Control? _opener;
    private InputPane? _keyboard;
    private bool _pin;
    private bool _backPressed;
    public bool IsOpen => ModalLayer.Visibility == Visibility.Visible;
    public event Action<string>? Confirmed;
    private Control Field => _pin ? PinInput : NameInput;

    public ProfileTextDialog()
    {
        InitializeComponent();
        _actions = Rail.Attach(Actions);
        _actions.Pin = false;
        Unloaded += (_, _) => Hide(restoreFocus: false);
    }

    public void Show(string value, bool pin)
    {
        Hide(restoreFocus: false);
        _opener = FocusManager.GetFocusedElement() as Control;
        _pin = pin;
        Title.Text = pin ? "Profile PIN" : "Profile Name";
        NameInput.Visibility = pin ? Visibility.Collapsed : Visibility.Visible;
        PinInput.Visibility = pin ? Visibility.Visible : Visibility.Collapsed;
        if (pin) PinInput.Password = value; else NameInput.Text = value;
        UpdateConfirm();
        _focusRoot = Window.Current.Content;
        if (_focusRoot is not null) _focusRoot.GettingFocus += OnGettingFocus;
        _keyboard = InputPane.GetForCurrentView();
        _keyboard.Showing += OnKeyboardShowing;
        _keyboard.Hiding += OnKeyboardHiding;
        ModalLayer.Visibility = Visibility.Visible;
        IsHitTestVisible = true;
        FocusRetry.Until(this, () =>
        {
            if (!IsOpen) return true;
            if (!Field.Focus(FocusState.Programmatic)) return false;
            // The opener was selected; merely focusing Name/PIN in the form never reaches this call.
            _keyboard?.TryShow();
            return true;
        });
    }

    public void Hide(bool restoreFocus = true)
    {
        var wasOpen = IsOpen;
        ModalLayer.Visibility = Visibility.Collapsed;
        IsHitTestVisible = false;
        _backPressed = false;
        if (_focusRoot is not null) _focusRoot.GettingFocus -= OnGettingFocus;
        _focusRoot = null;
        if (_keyboard is { } keyboard)
        {
            keyboard.Showing -= OnKeyboardShowing;
            keyboard.Hiding -= OnKeyboardHiding;
            if (wasOpen) keyboard.TryHide();
        }
        _keyboard = null;
        ModalLayer.Padding = new Thickness(0);
        var opener = _opener;
        _opener = null;
        if (wasOpen && restoreFocus && opener is not null)
            FocusRetry.Until(opener, () => IsOpen || opener.Focus(FocusState.Programmatic));
        PinInput.Password = string.Empty;
    }

    public bool Dismiss()
    {
        if (!IsOpen) return false;
        Hide();
        return true;
    }

    private void OnGettingFocus(UIElement sender, GettingFocusEventArgs args)
    {
        try
        {
            if (!IsOpen || SettingsInput.IsWithin(args.NewFocusedElement, this)) return;
            if (!args.TrySetNewFocusedElement(Field)) args.Cancel = true;
        }
        catch (Exception ex) { Log.Error("Profile text focus", ex); }
    }

    private void OnKeyboardShowing(InputPane sender, InputPaneVisibilityEventArgs args)
    {
        ModalLayer.Padding = new Thickness(0, 0, 0, args.OccludedRect.Height);
        args.EnsuredFocusedElementInView = true;
    }

    private void OnKeyboardHiding(InputPane sender, InputPaneVisibilityEventArgs args) => ModalLayer.Padding = new Thickness(0);
    private void OnTextChanged(object sender, TextChangedEventArgs args) => UpdateConfirm();
    private void OnPasswordChanged(object sender, RoutedEventArgs args) => UpdateConfirm();
    private void UpdateConfirm()
    {
        if (DoneButton is not null) DoneButton.IsEnabled = _pin || !string.IsNullOrWhiteSpace(NameInput.Text);
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is VirtualKey.Escape or VirtualKey.GamepadB)
        {
            args.Handled = true;
            _backPressed = true;
        }
        else if (args.Key == VirtualKey.Enter && ReferenceEquals(FocusManager.GetFocusedElement(), Field))
        {
            args.Handled = true;
            Confirm();
        }
        else if (SettingsInput.VerticalStep(args.Key) is var step && step != 0)
        {
            args.Handled = true;
            if (step < 0) Field.Focus(FocusState.Keyboard);
            else _actions.Focus(FocusState.Keyboard);
        }
        else if (args.Key == VirtualKey.Tab) args.Handled = true;
    }

    private void OnPreviewKeyUp(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is not (VirtualKey.Escape or VirtualKey.GamepadB)) return;
        args.Handled = true;
        if (_backPressed) Dismiss();
    }

    private void OnBackgroundTapped(object sender, TappedRoutedEventArgs args)
    {
        if (!ReferenceEquals(args.OriginalSource, ModalLayer)) return;
        args.Handled = true;
        Dismiss();
    }

    private void OnCancelClick(object sender, RoutedEventArgs args) => Dismiss();
    private void OnDoneClick(object sender, RoutedEventArgs args) => Confirm();
    private void Confirm()
    {
        if (!IsOpen || !DoneButton.IsEnabled) return;
        var value = _pin ? PinInput.Password : NameInput.Text.Trim();
        Hide();
        Confirmed?.Invoke(value);
    }
}
