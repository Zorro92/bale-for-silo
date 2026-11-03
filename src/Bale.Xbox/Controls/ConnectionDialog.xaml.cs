using Bale.Xbox.Services;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace Bale.Xbox.Controls;

/// <summary>TvServerSetupScreen.kt / TvOptionDialog.kt; focus stays inside the visible consent/removal modal.</summary>
public sealed partial class ConnectionDialog : UserControl
{
    private readonly Rail _httpActions;
    private UIElement? _focusRoot;
    private bool _backPressed;
    private bool _http;
    private Control Confirm => _http ? HttpConfirm : RemoveConfirm;
    private Control Cancel => _http ? HttpCancel : RemoveCancel;
    public bool IsOpen => ModalLayer.Visibility == Visibility.Visible;
    public event Action? Confirmed;
    public event Action? Dismissed;

    public ConnectionDialog()
    {
        InitializeComponent();
        _httpActions = Rail.Attach(HttpActions);
        _httpActions.Pin = false;
        Unloaded += (_, _) => Hide();
    }

    public void ShowHttp(string host)
    {
        HttpMessage.Text = $"Your password and what you watch will be sent unencrypted to {host}. Only do this on a network you trust.";
        Show(http: true);
    }

    public void ShowRemove(string name, bool active)
    {
        RemoveTitle.Text = (active ? $"Sign out & remove {name}?" : $"Remove {name}?").ToUpperInvariant();
        RemoveLabel.Text = active ? "Sign out & remove" : "Remove";
        RemoveMessage.Text = active ? "This is the server you're signed into — removing it will sign you out of it." : string.Empty;
        RemoveMessage.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        Show(http: false);
    }

    public void ShowDeleteProfile(string name)
    {
        // TvProfileSelectionScreen.kt also uses TvOptionDialog.kt: destructive option first.
        RemoveTitle.Text = $"Delete {name}?".ToUpperInvariant();
        RemoveLabel.Text = "Delete profile";
        RemoveMessage.Text = "This cannot be undone.";
        RemoveMessage.Visibility = Visibility.Visible;
        Show(http: false);
    }

    private void Show(bool http)
    {
        Hide();
        _http = http;
        HttpCard.Visibility = http ? Visibility.Visible : Visibility.Collapsed;
        RemoveCard.Visibility = http ? Visibility.Collapsed : Visibility.Visible;
        _httpActions.Remembered = HttpConfirm;
        _focusRoot = Window.Current.Content;
        if (_focusRoot is not null) _focusRoot.GettingFocus += OnGettingFocus;
        ModalLayer.Visibility = Visibility.Visible;
        IsHitTestVisible = true;
        FocusRetry.Until(this, () => !IsOpen || Confirm.Focus(FocusState.Programmatic));
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
            if (!IsOpen || SettingsInput.IsWithin(args.NewFocusedElement, this)) return;
            if (!args.TrySetNewFocusedElement(Confirm)) args.Cancel = true;
        }
        catch (Exception ex) { Log.Error("Connection dialog focus", ex); }
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (SettingsInput.IsBack(args.Key))
        {
            args.Handled = true;
            _backPressed = true;
        }
        else if (SettingsInput.VerticalStep(args.Key) is var step && step != 0)
        {
            args.Handled = true;
            if (!_http) (step > 0 ? Cancel : Confirm).Focus(FocusState.Keyboard);
        }
        else if (args.Key == VirtualKey.Tab || (!_http && SettingsInput.HorizontalStep(args.Key) != 0)) args.Handled = true;
    }

    private void OnPreviewKeyUp(object sender, KeyRoutedEventArgs args)
    {
        if (!SettingsInput.IsBack(args.Key)) return;
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
    private void OnConfirmClick(object sender, RoutedEventArgs args)
    {
        if (!IsOpen) return;
        Hide();
        Confirmed?.Invoke();
    }
}
