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
    private bool _focusCancel;
    private Control Confirm => _http ? HttpConfirm : RemoveConfirm;
    private Control Cancel => _http ? HttpCancel : RemoveCancel;
    public bool IsOpen => ModalLayer.Visibility == Visibility.Visible;
    public event Action? Confirmed;
    /// <summary>The account page's second action (Sign Out).</summary>
    public event Action? Alternate;
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

    /// <summary>TvAccountsScreen.kt confirmRemove: Remove server signs out of every account there; Cancel takes focus.</summary>
    public void ShowRemove(string name)
    {
        RemoveTitle.Text = $"Remove {name}?".ToUpperInvariant();
        RemoveLabel.Text = "Remove server";
        RemoveMessage.Text = "Signs out of every account on this server and removes it from this TV.";
        RemoveMessage.Visibility = Visibility.Visible;
        Show(http: false, focusCancel: true);
    }

    /// <summary>TvAccountsScreen.kt openAccount: who it is and its profiles, with Sign in again and Sign Out.</summary>
    public void ShowAccount(string name, string details)
    {
        RemoveTitle.Text = name.ToUpperInvariant();
        RemoveLabel.Text = "Sign in again";
        RemoveMessage.Text = details;
        RemoveMessage.Visibility = string.IsNullOrEmpty(details) ? Visibility.Collapsed : Visibility.Visible;
        AlternateLabel.Text = "Sign Out";
        Show(http: false, alternate: true);
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

    private void Show(bool http, bool focusCancel = false, bool alternate = false)
    {
        Hide();
        _http = http;
        _focusCancel = focusCancel;
        RemoveAlternate.Visibility = alternate ? Visibility.Visible : Visibility.Collapsed;
        HttpCard.Visibility = http ? Visibility.Visible : Visibility.Collapsed;
        RemoveCard.Visibility = http ? Visibility.Collapsed : Visibility.Visible;
        _httpActions.Remembered = HttpConfirm;
        _focusRoot = Window.Current.Content;
        if (_focusRoot is not null) _focusRoot.GettingFocus += OnGettingFocus;
        ModalLayer.Visibility = Visibility.Visible;
        IsHitTestVisible = true;
        FocusRetry.Until(this, () => !IsOpen || (_focusCancel ? Cancel : Confirm).Focus(FocusState.Programmatic));
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
            if (!args.TrySetNewFocusedElement(_focusCancel ? Cancel : Confirm)) args.Cancel = true;
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
            if (!_http)
            {
                // Rows in order; Up/Down stop at the ends (never sideways).
                var rows = RemoveAlternate.Visibility == Visibility.Visible ? new Control[] { RemoveConfirm, RemoveAlternate, RemoveCancel } : new Control[] { RemoveConfirm, RemoveCancel };
                var index = Array.FindIndex(rows, row => row.FocusState != FocusState.Unfocused);
                rows[Math.Clamp(index + step, 0, rows.Length - 1)].Focus(FocusState.Keyboard);
            }
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
    private void OnAlternateClick(object sender, RoutedEventArgs args)
    {
        if (!IsOpen) return;
        Hide();
        Alternate?.Invoke();
    }

    private void OnConfirmClick(object sender, RoutedEventArgs args)
    {
        if (!IsOpen) return;
        Hide();
        Confirmed?.Invoke();
    }
}
