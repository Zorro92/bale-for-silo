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

/// <summary><c>playback.buffering_quality_mode</c> as the server resolves it for this profile and device.</summary>
/// <param name="Value">The effective mode; the default (<c>always</c>) before a read or on a server without the setting.</param>
/// <param name="DeviceOverride">True when this device stores its own value (scope profile_device).</param>
/// <param name="Supported">
/// The server defines the key. Settings shows the row only then; a server without it plays <c>always</c>
/// and nothing on this device stands in for it (buffering-quality design, "The setting").
/// </param>
public sealed record BufferingQualitySetting(BufferingQualityMode Value = BufferingQualityModes.Default,
    bool DeviceOverride = false, bool Supported = false);

/// <summary>
/// The playback settings the player uses, read from <c>/api/v2/settings/values/effective</c>.
/// Writes follow the Android TV app (PlayerSettingsStore.kt): a change on the TV stores a
/// profile_device override, and "Use profile setting" deletes that override.
/// </summary>
public sealed class PlaybackSettingsFeed(SiloApiClient api)
{
    public const string AutoPlayNextKey = "playback.auto_play_next";
    public const string BufferingQualityModeKey = BufferingQualityModes.Key;
    public const string DeviceScope = "profile_device";

    private readonly object _sync = new();
    private BooleanSetting _autoPlayNext = new(true);
    private BufferingQualitySetting _bufferingQuality = new();
    private int _generation;

    /// <summary>Last known auto-play value; defaults to on (the server's default) before a read.</summary>
    public BooleanSetting AutoPlayNext { get { lock (_sync) return _autoPlayNext; } }

    /// <summary>
    /// Last known buffering-quality mode. A start never waits for a read: until one lands it plays the last
    /// mode seen for this profile in this session, and the default before that.
    /// </summary>
    public BufferingQualitySetting BufferingQuality { get { lock (_sync) return _bufferingQuality; } }

    public void Reset()
    {
        lock (_sync)
        {
            _generation++;
            _autoPlayNext = new(true);
            _bufferingQuality = new();
        }
    }

    /// <summary>
    /// Reads the effective values of auto-play and the buffering-quality mode; a failed read keeps the last
    /// known values. A server whose manifest lacks the mode answers 404 for the whole read, so that is retried
    /// with auto-play alone and the mode marked unsupported.
    /// </summary>
    public async Task<BooleanSetting> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var generation = Generation();
        EffectiveSettingCollection? collection;
        var supported = true;
        try
        {
            collection = await api.Api.V2.Settings.Values.Effective.GetAsync(
                c => c.QueryParameters.Keys = [AutoPlayNextKey, BufferingQualityModeKey], cancellationToken).ConfigureAwait(false);
        }
        catch (Problem problem) when (problem.ResponseStatusCode == 404)
        {
            supported = false;
            collection = await api.Api.V2.Settings.Values.Effective.GetAsync(
                c => c.QueryParameters.Keys = [AutoPlayNextKey], cancellationToken).ConfigureAwait(false);
        }
        var row = collection?.Items?.FirstOrDefault(item => item.Key == AutoPlayNextKey);
        var setting = row is null ? new BooleanSetting(true, Known: true)
            : new BooleanSetting(Boolean(row.Value) ?? true, row.Scope == DeviceScope, true);
        var modeRow = supported ? collection?.Items?.FirstOrDefault(item => item.Key == BufferingQualityModeKey) : null;
        var mode = supported
            ? new BufferingQualitySetting(BufferingQualityModes.FromWire(Text(modeRow?.Value)) ?? BufferingQualityModes.Default,
                modeRow?.Scope == DeviceScope, true)
            : new BufferingQualitySetting();
        lock (_sync)
        {
            if (generation == _generation) _bufferingQuality = mode;
        }
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

    /// <summary>Picking a mode on the TV stores a device override (TvSettingsScreen.kt's picker sheet).</summary>
    public async Task<BufferingQualitySetting> SetBufferingQualityModeAsync(BufferingQualityMode mode,
        CancellationToken cancellationToken = default)
    {
        var generation = Generation();
        await api.Api.V2.Settings.Values[BufferingQualityModeKey].PutAsync(
            new SettingValueWrite { Value = new UntypedString(BufferingQualityModes.Wire(mode)) },
            c => c.QueryParameters.Scope = PutScopeQueryParameterType.Profile_device, cancellationToken).ConfigureAwait(false);
        var setting = new BufferingQualitySetting(mode, true, true);
        lock (_sync)
        {
            if (generation == _generation) _bufferingQuality = setting;
        }
        return setting;
    }

    /// <summary>"Use profile setting" in the mode picker: drop this device's value, then reread.</summary>
    public async Task<BufferingQualitySetting> UseProfileBufferingQualityModeAsync(CancellationToken cancellationToken = default)
    {
        await api.Api.V2.Settings.Values[BufferingQualityModeKey].DeleteAsync(
            c => c.QueryParameters.Scope = DeleteScopeQueryParameterType.Profile_device, cancellationToken).ConfigureAwait(false);
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        return BufferingQuality;
    }

    internal static bool? Boolean(UntypedNode? node) => node switch
    {
        UntypedBoolean value => value.GetValue(),
        UntypedString text when bool.TryParse(text.GetValue(), out var parsed) => parsed,
        _ => null,
    };

    internal static string? Text(UntypedNode? node) => node is UntypedString text ? text.GetValue() : null;

    /// <summary>Tests: adopt a mode without a read.</summary>
    internal void StoreBufferingQuality(BufferingQualitySetting setting)
    {
        lock (_sync) _bufferingQuality = setting;
    }

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
