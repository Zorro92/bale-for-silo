using Bale.Core.Api.Models;
using Bale.Core.Profiles;
using Bale.Xbox.Controls;
using Bale.Xbox.Views;
using Windows.UI.Core;

namespace Bale.Xbox.Services;

/// <summary>TvHouseholdReverifyHost.kt: one shared PIN UI, with proof scoped to the household, not the selected viewer.</summary>
internal sealed class ProfileManagementPrompt
{
    private readonly ProfilePinDialog _dialog;
    private readonly Func<Profile, ProfileView> _view;
    private HouseholdManagementSession? _management;
    private CancellationTokenSource? _lifetime;
    private bool _showing;

    public ProfileManagementPrompt(ProfilePinDialog dialog, Func<Profile, ProfileView> view)
    {
        _dialog = dialog;
        _view = view;
        dialog.PinEntered += OnPinEntered;
        dialog.Dismissed += OnDismissed;
    }

    public void Activate(HouseholdManagementSession management)
    {
        Deactivate();
        _management = management;
        _lifetime = new();
        management.Changed += OnChanged;
        OnChanged();
    }

    public void Deactivate()
    {
        if (_management is { } management) management.Changed -= OnChanged;
        _management = null;
        _lifetime?.Cancel();
        _lifetime?.Dispose();
        _lifetime = null;
        if (_showing) _dialog.Hide(restoreFocus: false);
        _showing = false;
    }

    public bool Dismiss() => _showing && _dialog.Dismiss();

    private async void OnChanged()
    {
        var management = _management;
        try
        {
            await _dialog.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (management is null || !ReferenceEquals(_management, management)) return;
                if (management.Reverify is not { } state)
                {
                    if (_showing) _dialog.Hide();
                    _showing = false;
                    return;
                }
                if (!_showing)
                {
                    _showing = true;
                    _dialog.Show(_view(state.Profile), "Enter this PIN to manage profiles");
                }
                if (state.Error is { } error) _dialog.ShowError(error);
                else _dialog.SetVerifying(state.IsVerifying);
            });
        }
        catch (Exception ex) { Log.Error("Profile management prompt", ex); }
    }

    private async void OnPinEntered(string pin)
    {
        if (!_showing || _management is not { } management || _lifetime is not { } lifetime) return;
        try { await management.SubmitPinAsync(pin, lifetime.Token); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Log.Error("Profile management PIN", ex); }
    }

    private void OnDismissed()
    {
        if (!_showing) return;
        _showing = false;
        _management?.End();
    }
}
