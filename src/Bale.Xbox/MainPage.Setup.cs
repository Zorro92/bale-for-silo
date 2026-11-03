using Bale.Core.Connection;
using Bale.Xbox.Services;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;

namespace Bale.Xbox;

public sealed partial class MainPage
{
    private CancellationTokenSource? _probeLife;
    private ServerSetupScheme _scheme;
    private ServerSetupResult? _pendingServer;
    private string? _pendingName;
    private bool _pendingActivation;
    private LanServerDiscovery? _discovery;
    private IReadOnlyList<DiscoveredServer> _nearby = [];

    private void ShowServerEntry(string? server, string? error = null, bool discover = true)
    {
        HideKeyboard();
        TvNameChip.Visibility = Visibility.Visible;
        ServerPanel.Visibility = Visibility.Visible;
        CodePanel.Visibility = Visibility.Collapsed;
        ServerBox.Text = server ?? string.Empty;
        ServerError.Text = error ?? string.Empty;
        ServerError.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        ServerBox.ApplyTemplate();
        VisualStateManager.GoToState(ServerBox, error is null ? "Valid" : "Invalid", false);
        SetConnecting(false);
        if (discover && _preview is null)
        {
            _discovery ??= new LanServerDiscovery(Dispatcher, UpdateNearby);
            _discovery.Start();
        }
        RestoreFocus();
    }

    private void StopDiscovery()
    {
        _discovery?.Dispose();
        _discovery = null;
    }

    private void OnServerTextChanged(object sender, TextChangedEventArgs args)
    {
        if (ServerError is null || ServerSecurityHint is null || PortBox is null) return;
        ServerError.Visibility = Visibility.Collapsed;
        VisualStateManager.GoToState(ServerBox, "Valid", false);
        ServerSecurityHint.Text = ServerSetupAddress.UsesCleartext(ServerBox.Text, _scheme, PortBox.Text)
            ? "This address uses unencrypted HTTP. Fine on a trusted home network; avoid it on public Wi-Fi."
            : "Secure HTTPS is tried automatically.";
    }

    private void OnAdvancedClick(object sender, RoutedEventArgs args)
    {
        if (_connecting) return;
        var visible = AdvancedPanel.Visibility != Visibility.Visible;
        AdvancedPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        AdvancedButton.Content = visible ? "Hide advanced options" : "Advanced options";
    }

    private void OnSchemeClick(object sender, RoutedEventArgs args)
    {
        if (!_connecting && sender is ToggleButton { Tag: string value } && Enum.TryParse<ServerSetupScheme>(value, out var scheme)) _scheme = scheme;
        AutoButton.IsChecked = _scheme == ServerSetupScheme.Auto;
        HttpsButton.IsChecked = _scheme == ServerSetupScheme.Https;
        HttpButton.IsChecked = _scheme == ServerSetupScheme.Http;
        OnServerTextChanged(this, null!);
    }

    private void SetConnecting(bool connecting)
    {
        _connecting = connecting;
        ServerBox.IsReadOnly = PortBox.IsReadOnly = connecting;
        ConnectButton.Content = connecting ? "Connecting…" : "Connect";
    }

    private async void OnConnectClick(object sender, RoutedEventArgs args)
    {
        if (_connecting || HttpPrompt.IsOpen) return;
        HideKeyboard();
        try { await ProbeServerAsync(ServerSetupAddress.Candidates(ServerBox.Text, _scheme, PortBox.Text), ConnectButton); }
        catch (ArgumentException ex)
        {
            ServerError.Text = ex.Message;
            ServerError.Visibility = Visibility.Visible;
            VisualStateManager.GoToState(ServerBox, "Invalid", false);
            RestoreFocus();
        }
    }

    private async Task ProbeServerAsync(IReadOnlyList<Uri> candidates, Button opener)
    {
        _probeLife?.Cancel();
        _probeLife?.Dispose();
        var lifetime = _probeLife = new CancellationTokenSource();
        SetConnecting(true);
        try
        {
            ServerSetupResult result;
            string? name;
            if (_preview is not null)
            {
                result = new ServerSetupResult(candidates[0]);
                name = "Example server";
            }
            else
            {
                using var probe = new ServerSetupProbe(log: message => Log.Info(message));
                result = await probe.ProbeAsync(candidates, lifetime.Token);
                name = result.Server is { } found ? await probe.NameAsync(found, lifetime.Token) : null;
            }
            if (!_active || lifetime.IsCancellationRequested || !ReferenceEquals(_probeLife, lifetime)) return;
            _pendingName = name;
            SetConnecting(false);
            if (result.Server is not { } server)
            {
                ShowSetupError(result.Error ?? ServerSetupProbe.NetworkError);
                return;
            }
            if (result.NeedsSetup)
            {
                // First-admin account creation is a separate flow, not a password-login fallback.
                ShowSetupError("This server has no accounts yet. Set it up on your phone or in a browser, then try again.");
                return;
            }
            _pendingActivation = true;
            _pendingServer = result;
            if (Servers.RequiresHttpApproval(server))
            {
                HideKeyboard();
                Log.Info("HTTP setup gate awaiting exact-origin consent");
                ShowHttpPrompt(server, opener);
                return;
            }
            await FinishServerSelectionAsync(result);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Info($"Server setup failed type={ex.GetType().Name}");
            if (_active && !lifetime.IsCancellationRequested) ShowSetupError("Unable to save the server. Please try again.");
        }
        finally
        {
            if (ReferenceEquals(_probeLife, lifetime)) SetConnecting(false);
        }
    }

    private async void OnHttpConfirmed()
    {
        _httpPromptOpener = null;
        if (!_active || _pendingServer is not { Server: { } server } result) return;
        _pendingServer = null;
        try
        {
            Servers.ApproveHttp(server);
            Log.Info($"HTTP exact-origin consent saved preview={_preview is not null}");
            if (_pendingActivation) await FinishServerSelectionAsync(result);
            else await ResolveServerAsync(server);
        }
        catch (Exception ex)
        {
            Log.Info($"HTTP consent or selection failed type={ex.GetType().Name}");
            ShowSetupError("Unable to save the server. Please try again.");
        }
    }

    private async Task FinishServerSelectionAsync(ServerSetupResult result)
    {
        _pendingServer = null;
        if (result.Server is not { } server || !_active) return;
        if (_preview is not null)
        {
            Servers.AddAndActivate(server, _pendingName, result.VerifiedServerId);
            _loginServer = server;
            await StartLoginAsync(server);
            return;
        }
        Session.ActivateServer(server, _pendingName, result.VerifiedServerId);
        _expiredNotice = false;
        Frame.BackStack.Clear(); // Old server pages must never be reachable after committing a selection.
        await ResolveServerAsync(server);
    }

    private void ShowSetupError(string error)
    {
        ServerError.Text = error;
        ServerError.Visibility = Visibility.Visible;
        SetConnecting(false);
        ConnectButton.Focus(FocusState.Programmatic);
    }

    private void OnServerBoxPreviewKeyDown(object sender, KeyRoutedEventArgs args) => OnSetupControlPreviewKeyDown(sender, args);
    private void OnServerActionsPreviewKeyDown(object sender, KeyRoutedEventArgs args) => OnSetupControlPreviewKeyDown(sender, args);

    private void OnSetupControlPreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        var field = sender is TextBox text ? text : null;
        if (_keyboardVisible)
        {
            if (field is not null && args.Key == VirtualKey.Enter)
            {
                args.Handled = true;
                OnConnectClick(sender, args);
            }
            return;
        }
        if (field is not null && args.Key is VirtualKey.Enter or VirtualKey.GamepadA)
        {
            args.Handled = true;
            if (!_connecting) _inputPane?.TryShow();
            return;
        }
        var step = SettingsInput.VerticalStep(args.Key);
        if (step != 0)
        {
            args.Handled = true;
            var current = FocusManager.GetFocusedElement();
            Control? target = null;
            var advanced = AdvancedPanel.Visibility == Visibility.Visible;
            if (ReferenceEquals(current, ServerBox) && step > 0) target = AdvancedButton;
            else if (ReferenceEquals(current, AdvancedButton)) target = step < 0 ? ServerBox : advanced ? AutoButton : ConnectButton;
            else if (SettingsInput.IsWithin(current as DependencyObject, SchemeActions))
            {
                // Only Auto aligns with the port/Advanced column. Other scheme columns stop vertically.
                if (ReferenceEquals(current, AutoButton)) target = step < 0 ? AdvancedButton : PortBox;
            }
            else if (ReferenceEquals(current, PortBox)) target = step < 0 ? AutoButton : ConnectButton;
            else if (SettingsInput.IsWithin(current as DependencyObject, ServerActions))
            {
                // Back is a second column, with no field or nearby row above/below it.
                if (ReferenceEquals(current, ConnectButton)) target = step < 0 ? advanced ? PortBox : AdvancedButton
                    : _nearby.Count > 0 ? NearbyFirst : null;
            }
            else if (ReferenceEquals(current, NearbyFirst)) target = step < 0 ? ConnectButton : _nearby.Count > 1 ? NearbySecond : null;
            else if (ReferenceEquals(current, NearbySecond) && step < 0) target = NearbyFirst;
            target?.Focus(FocusState.Programmatic);
        }
        else if ((field is not null || ReferenceEquals(sender, AdvancedButton) || ReferenceEquals(sender, NearbyFirst) || ReferenceEquals(sender, NearbySecond))
            && SettingsInput.HorizontalStep(args.Key) != 0) args.Handled = true;
    }

    private void UpdateNearby(IReadOnlyList<DiscoveredServer> servers)
    {
        if (!_active || ServerPanel.Visibility != Visibility.Visible) return;
        var focus = FocusManager.GetFocusedElement();
        var focusedId = ReferenceEquals(focus, NearbyFirst) ? _nearby.ElementAtOrDefault(0)?.ServerId
            : ReferenceEquals(focus, NearbySecond) ? _nearby.ElementAtOrDefault(1)?.ServerId : null;
        _nearby = servers.Take(2).ToArray();
        NearbyPanel.Visibility = _nearby.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NearbySecond.Visibility = _nearby.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (_nearby.Count > 0)
        {
            NearbyFirstName.Text = _nearby[0].Name;
            NearbyFirstAddress.Text = "On this network · " + _nearby[0].Server.Authority;
        }
        if (_nearby.Count > 1)
        {
            NearbySecondName.Text = _nearby[1].Name;
            NearbySecondAddress.Text = "On this network · " + _nearby[1].Server.Authority;
        }
        if (focusedId is not null && !HttpPrompt.IsOpen)
        {
            var index = Array.FindIndex(_nearby.ToArray(), row => row.ServerId == focusedId);
            (index == 0 ? NearbyFirst : index == 1 ? NearbySecond : ConnectButton).Focus(FocusState.Programmatic);
        }
    }

    private async void OnNearbyClick(object sender, RoutedEventArgs args)
    {
        if (_connecting || HttpPrompt.IsOpen) return;
        var index = ReferenceEquals(sender, NearbySecond) ? 1 : 0;
        if (_nearby.ElementAtOrDefault(index) is not { } row) return;
        ServerBox.Text = ServerAddress.ToBaseUrl(row.Server);
        await ProbeServerAsync([row.Server], index == 1 ? NearbySecond : NearbyFirst); // Still run the contract/setup and HTTP gates before saving anything.
    }

    private void ShowNearbyPreview() => UpdateNearby([
        new DiscoveredServer("preview-first", "Living room", new Uri("http://192.0.2.10:8090")),
        new DiscoveredServer("preview-second", "Media room", new Uri("http://192.0.2.11:8090")),
    ]);
}
