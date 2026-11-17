using Bale.Core.Api;
using Bale.Core.Api.Api.V2.Settings.Values.Item;
using Bale.Core.Api.Models;
using Microsoft.Kiota.Abstractions.Serialization;

namespace Bale.Core.Settings;

/// <summary>A boolean setting as the server resolves it for this profile and device.</summary>
/// <param name="Value">The effective value.</param>
/// <param name="DeviceOverride">True when this device stores its own value (scope profile_device).</param>
/// <param name="Known">False until a read succeeded; <paramref name="Value"/> is then the default.</param>
public sealed record BooleanSetting(bool Value, bool DeviceOverride = false, bool Known = false);

/// <summary>
/// The playback settings the player uses, read from <c>/api/v2/settings/values/effective</c>.
/// Writes follow the Android TV app (PlayerSettingsStore.kt): a change on the TV stores a
/// profile_device override, and "Use profile setting" deletes that override.
/// </summary>
public sealed class PlaybackSettingsFeed(SiloApiClient api)
{
    public const string AutoPlayNextKey = "playback.auto_play_next";
    public const string DeviceScope = "profile_device";

    private readonly object _sync = new();
    private BooleanSetting _autoPlayNext = new(true);
    private int _generation;

    /// <summary>Last known auto-play value; defaults to on (the server's default) before a read.</summary>
    public BooleanSetting AutoPlayNext { get { lock (_sync) return _autoPlayNext; } }

    public void Reset()
    {
        lock (_sync)
        {
            _generation++;
            _autoPlayNext = new(true);
        }
    }

    /// <summary>Reads the effective value; a failed read keeps the last known value.</summary>
    public async Task<BooleanSetting> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var generation = Generation();
        var collection = await api.Api.V2.Settings.Values.Effective.GetAsync(
            c => c.QueryParameters.Keys = [AutoPlayNextKey], cancellationToken).ConfigureAwait(false);
        var row = collection?.Items?.FirstOrDefault(item => item.Key == AutoPlayNextKey);
        var setting = row is null ? new BooleanSetting(true, Known: true)
            : new BooleanSetting(Boolean(row.Value) ?? true, row.Scope == DeviceScope, true);
        return Store(generation, setting);
    }

    /// <summary>Stores a device override (what toggling the switch on the TV does).</summary>
    public async Task<BooleanSetting> SetAutoPlayNextAsync(bool value, CancellationToken cancellationToken = default)
    {
        var generation = Generation();
        await api.Api.V2.Settings.Values[AutoPlayNextKey].PutAsync(new SettingValueWrite { Value = new UntypedBoolean(value) },
            c => c.QueryParameters.Scope = PutScopeQueryParameterType.Profile_device, cancellationToken).ConfigureAwait(false);
        return Store(generation, new BooleanSetting(value, true, true));
    }

    /// <summary>Drops the device override so the profile's value applies again, then rereads it.</summary>
    public async Task<BooleanSetting> UseProfileSettingAsync(CancellationToken cancellationToken = default)
    {
        await api.Api.V2.Settings.Values[AutoPlayNextKey].DeleteAsync(
            c => c.QueryParameters.Scope = DeleteScopeQueryParameterType.Profile_device, cancellationToken).ConfigureAwait(false);
        return await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static bool? Boolean(UntypedNode? node) => node switch
    {
        UntypedBoolean value => value.GetValue(),
        UntypedString text when bool.TryParse(text.GetValue(), out var parsed) => parsed,
        _ => null,
    };

    private int Generation() { lock (_sync) return _generation; }

    private BooleanSetting Store(int generation, BooleanSetting setting)
    {
        lock (_sync)
        {
            if (generation == _generation) _autoPlayNext = setting;
            return setting;
        }
    }
}
