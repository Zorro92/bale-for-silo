using Bale.Core.Api;
using Bale.Core.Api.Api.V2.Settings.Values.Item;
using Bale.Core.Api.Models;
using Bale.Core.Playback;
using Microsoft.Kiota.Abstractions.Serialization;

namespace Bale.Core.Settings;

/// <summary>A boolean setting as the server resolves it for this profile and device.</summary>
/// <param name="Value">The effective value.</param>
/// <param name="DeviceOverride">True when this device stores its own value (scope profile_device).</param>
/// <param name="Known">False until a read succeeded; <paramref name="Value"/> is then the default.</param>
public sealed record BooleanSetting(bool Value, bool DeviceOverride = false, bool Known = false);

/// <summary>An integer setting as the server resolves it for this profile and device.</summary>
/// <param name="Value">The effective value.</param>
/// <param name="DeviceOverride">True when this device stores its own value (scope profile_device).</param>
/// <param name="Known">False until a read succeeded; <paramref name="Value"/> is then the default.</param>
public sealed record IntegerSetting(int Value, bool DeviceOverride = false, bool Known = false);

/// <summary><c>playback.buffering_quality_mode</c> as the server resolves it for this profile and device.</summary>
/// <param name="Value">The effective mode; the default (<c>always</c>) before a read or on a server without the setting.</param>
/// <param name="DeviceOverride">True when this device stores its own value (scope profile_device).</param>
/// <param name="Supported">
/// The server defines the key. Settings shows the row only then; a server without it plays <c>always</c>
/// and nothing on this device stands in for it (buffering-quality design, "The setting").
/// </param>
public sealed record BufferingQualitySetting(BufferingQualityMode Value = BufferingQualityModes.Default,
    bool DeviceOverride = false, bool Supported = false);

/// <summary><c>playback.intro_skip_mode</c> as the server resolves it for this profile and device.</summary>
/// <param name="Value">The effective mode; the contract default (<c>ask</c>) before a read or for a value Bale does not know.</param>
/// <param name="DeviceOverride">True when this device stores its own value (scope profile_device).</param>
/// <param name="Known">False until a read succeeded.</param>
public sealed record IntroSkipSetting(IntroSkipMode Value = IntroSkipModes.Default, bool DeviceOverride = false, bool Known = false);

/// <summary>
/// The playback settings the player uses, read from <c>/api/v2/settings/values/effective</c>.
/// Writes follow the Android TV app (PlayerSettingsStore.kt): a change on the TV stores a
/// profile_device override, and "Use profile setting" deletes that override.
/// </summary>
/// <summary>Small device-local key/value storage for settings memory (local settings on the console).</summary>
public interface ISettingsMemory
{
    string? Read(string key);
    void Write(string key, string value);
}

public sealed class PlaybackSettingsFeed(SiloApiClient api)
{
    public const string AutoPlayNextKey = "playback.auto_play_next";
    public const string NextUpPromptSecondsKey = "playback.next_up_prompt_seconds";
    public const string IntroSkipModeKey = IntroSkipModes.Key;
    public const string AutoSkipCreditsKey = "playback.auto_skip_credits";
    public const string BufferingQualityModeKey = BufferingQualityModes.Key;
    public const string DeviceScope = "profile_device";

    /// <summary>
    /// Read on every refresh. All of them predate the v2 settings API (auto-play, the Show Next Up lead and Skip
    /// Credits came with the first manifest, Skip Intros with revision 7), so no server answers 404 for them.
    /// </summary>
    private static readonly string[] BaseKeys = [AutoPlayNextKey, NextUpPromptSecondsKey, IntroSkipModeKey, AutoSkipCreditsKey];

    private readonly object _sync = new();
    private readonly Func<string?>? _scope;
    private BooleanSetting _autoPlayNext = new(true);
    private IntegerSetting _nextUpPromptSeconds = new(NextUpPrompt.DefaultSeconds);
    private IntroSkipSetting _introSkip = new();
    private BooleanSetting _autoSkipCredits = new(false);
    private BufferingQualitySetting _bufferingQuality = new();
    private bool _bufferingRead;
    private int _generation;

    /// <summary>Last known auto-play value; defaults to on (the server's default) before a read.</summary>
    public BooleanSetting AutoPlayNext { get { lock (_sync) return _autoPlayNext; } }

    /// <summary>
    /// Last known Skip Intros mode (TvSettingsScreen.kt's picker); <c>ask</c>, the contract default, before a read, as
    /// AndroidPlayerSettingsStore.kt's introSkipModeFlow starts. The player reads it on every tick, so a change made in
    /// Settings or a read that lands mid-intro re-evaluates the intro at once (IntroAutoSkipController.kt).
    /// </summary>
    public IntroSkipSetting IntroSkip { get { lock (_sync) return _introSkip; } }

    /// <summary>
    /// Last known Skip Credits switch (<c>playback.auto_skip_credits</c>, default off): on, the player skips each
    /// credits occurrence by itself once (silo-apple PlayerViewModel.swift CreditsAutoSkipPolicy).
    /// </summary>
    public BooleanSetting AutoSkipCredits { get { lock (_sync) return _autoSkipCredits; } }

    /// <summary>
    /// Last known Show Next Up lead: seconds before the end of the file the Up Next card opens (0 = only at the end).
    /// Defaults to 30 before a read, as the TV apps' settings stores do (TvPlayerViewModel.kt nextUpPromptSeconds,
    /// PlayerSettings.swift). Read with auto-play: both keys came with the first settings manifest.
    /// </summary>
    public IntegerSetting NextUpPromptSeconds { get { lock (_sync) return _nextUpPromptSeconds; } }

    /// <summary>
    /// Device-local memory of the last buffering-quality mode seen per server and profile, so a start before the
    /// first read uses it instead of the default (the official apps keep the last mode on the device). The app sets it
    /// once at launch, before any client exists; null keeps the session-only behaviour.
    /// </summary>
    public static ISettingsMemory? DefaultMemory { get; set; }

    /// <summary>This feed's memory; <see cref="DefaultMemory"/> unless set (tests use their own).</summary>
    public ISettingsMemory? Memory { get; set; } = DefaultMemory;

    /// <param name="scope">The server and profile the values belong to (null while no profile is active).</param>
    public PlaybackSettingsFeed(SiloApiClient api, Func<string?>? scope) : this(api) => _scope = scope;

    /// <summary>
    /// Last known buffering-quality mode. A start never waits for a read: until one lands it plays the last mode seen
    /// for this profile (this session, else on this device), and the default before that.
    /// </summary>
    public BufferingQualitySetting BufferingQuality
    {
        get
        {
            lock (_sync)
            {
                if (_bufferingRead || MemoryKey() is not { } key
                    || BufferingQualityModes.FromWire(Memory?.Read(key)) is not { } remembered) return _bufferingQuality;
                return _bufferingQuality with { Value = remembered, Supported = true };
            }
        }
    }

    /// <summary>A local-settings key for this server and profile: a hash, so no address or id is stored in clear.</summary>
    private string? MemoryKey()
    {
        if (_scope?.Invoke() is not { Length: > 0 } scope) return null;
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(scope));
        return "buffering_mode." + Convert.ToHexString(hash, 0, 12).ToLowerInvariant();
    }

    private void Remember(BufferingQualitySetting setting)
    {
        // Only a mode the server defines is remembered: a server without the key plays the default.
        if (!setting.Supported || MemoryKey() is not { } key) return;
        try { Memory?.Write(key, BufferingQualityModes.Wire(setting.Value)); }
        catch (Exception) { /* Memory is a convenience; a failed write keeps the session value. */ }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _generation++;
            _autoPlayNext = new(true);
            _nextUpPromptSeconds = new(NextUpPrompt.DefaultSeconds);
            _introSkip = new();
            _autoSkipCredits = new(false);
            _bufferingQuality = new();
            _bufferingRead = false;
        }
    }

    /// <summary>
    /// Reads the effective values of auto-play, the Show Next Up lead, Skip Intros, Skip Credits and the
    /// buffering-quality mode; a failed read keeps the last known values. A server whose manifest lacks the mode
    /// answers 404 for the whole read, so that is retried without it and the mode marked unsupported.
    /// </summary>
    public async Task<BooleanSetting> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var generation = Generation();
        EffectiveSettingCollection? collection;
        var supported = true;
        try
        {
            collection = await api.Api.V2.Settings.Values.Effective.GetAsync(
                c => c.QueryParameters.Keys = [.. BaseKeys, BufferingQualityModeKey], cancellationToken).ConfigureAwait(false);
        }
        catch (Problem problem) when (problem.ResponseStatusCode == 404)
        {
            supported = false;
            collection = await api.Api.V2.Settings.Values.Effective.GetAsync(
                c => c.QueryParameters.Keys = [.. BaseKeys], cancellationToken).ConfigureAwait(false);
        }
        var row = collection?.Items?.FirstOrDefault(item => item.Key == AutoPlayNextKey);
        var setting = row is null ? new BooleanSetting(true, Known: true)
            : new BooleanSetting(Boolean(row.Value) ?? true, row.Scope == DeviceScope, true);
        var promptRow = collection?.Items?.FirstOrDefault(item => item.Key == NextUpPromptSecondsKey);
        var prompt = new IntegerSetting(Integer(promptRow?.Value) is { } seconds ? NextUpPrompt.Clamp(seconds) : NextUpPrompt.DefaultSeconds,
            promptRow?.Scope == DeviceScope, true);
        var introRow = collection?.Items?.FirstOrDefault(item => item.Key == IntroSkipModeKey);
        var intro = new IntroSkipSetting(IntroSkipModes.FromWire(Text(introRow?.Value)) ?? IntroSkipModes.Default,
            introRow?.Scope == DeviceScope, true);
        var creditsRow = collection?.Items?.FirstOrDefault(item => item.Key == AutoSkipCreditsKey);
        var credits = new BooleanSetting(Boolean(creditsRow?.Value) ?? false, creditsRow?.Scope == DeviceScope, true);
        var modeRow = supported ? collection?.Items?.FirstOrDefault(item => item.Key == BufferingQualityModeKey) : null;
        var mode = supported
            ? new BufferingQualitySetting(BufferingQualityModes.FromWire(Text(modeRow?.Value)) ?? BufferingQualityModes.Default,
                modeRow?.Scope == DeviceScope, true)
            : new BufferingQualitySetting();
        lock (_sync)
        {
            if (generation == _generation)
            {
                _nextUpPromptSeconds = prompt;
                _introSkip = intro;
                _autoSkipCredits = credits;
                _bufferingQuality = mode;
                _bufferingRead = true;
                Remember(mode);
            }
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
        await DeleteDeviceValueAsync(AutoPlayNextKey, cancellationToken).ConfigureAwait(false);
        return await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Picking a mode on the TV stores a device override (TvSettingsScreen.kt PlaybackPicker.IntroSkipMode).</summary>
    public async Task<IntroSkipSetting> SetIntroSkipModeAsync(IntroSkipMode mode, CancellationToken cancellationToken = default)
    {
        var generation = Generation();
        await api.Api.V2.Settings.Values[IntroSkipModeKey].PutAsync(
            new SettingValueWrite { Value = new UntypedString(IntroSkipModes.Wire(mode)) },
            c => c.QueryParameters.Scope = PutScopeQueryParameterType.Profile_device, cancellationToken).ConfigureAwait(false);
        var setting = new IntroSkipSetting(mode, true, true);
        lock (_sync)
        {
            if (generation == _generation) _introSkip = setting;
        }
        return setting;
    }

    /// <summary>"Use profile setting" in the Skip Intros picker: drop this device's value, then reread.</summary>
    public async Task<IntroSkipSetting> UseProfileIntroSkipModeAsync(CancellationToken cancellationToken = default)
    {
        await DeleteDeviceValueAsync(IntroSkipModeKey, cancellationToken).ConfigureAwait(false);
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        return IntroSkip;
    }

    /// <summary>The Skip Credits switch stores a device override (TvSettingsScreen.kt SettingsToggleRow).</summary>
    public async Task<BooleanSetting> SetAutoSkipCreditsAsync(bool value, CancellationToken cancellationToken = default)
    {
        var generation = Generation();
        await api.Api.V2.Settings.Values[AutoSkipCreditsKey].PutAsync(new SettingValueWrite { Value = new UntypedBoolean(value) },
            c => c.QueryParameters.Scope = PutScopeQueryParameterType.Profile_device, cancellationToken).ConfigureAwait(false);
        var setting = new BooleanSetting(value, true, true);
        lock (_sync)
        {
            if (generation == _generation) _autoSkipCredits = setting;
        }
        return setting;
    }

    /// <summary>Skip Credits' "Use Profile Setting" row: drop this device's value, then reread.</summary>
    public async Task<BooleanSetting> UseProfileAutoSkipCreditsAsync(CancellationToken cancellationToken = default)
    {
        await DeleteDeviceValueAsync(AutoSkipCreditsKey, cancellationToken).ConfigureAwait(false);
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        return AutoSkipCredits;
    }

    private Task DeleteDeviceValueAsync(string key, CancellationToken cancellationToken) =>
        api.Api.V2.Settings.Values[key].DeleteAsync(
            c => c.QueryParameters.Scope = DeleteScopeQueryParameterType.Profile_device, cancellationToken);

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
            if (generation == _generation)
            {
                _bufferingQuality = setting;
                _bufferingRead = true;
                Remember(setting);
            }
        }
        return setting;
    }

    /// <summary>"Use profile setting" in the mode picker: drop this device's value, then reread.</summary>
    public async Task<BufferingQualitySetting> UseProfileBufferingQualityModeAsync(CancellationToken cancellationToken = default)
    {
        await DeleteDeviceValueAsync(BufferingQualityModeKey, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// A whole number: the JSON parser types one as an integer or long, and <c>30.0</c> as a decimal. A fraction
    /// or anything else is not one.
    /// </summary>
    internal static long? Integer(UntypedNode? node) => node switch
    {
        UntypedInteger value => value.GetValue(),
        UntypedLong value => value.GetValue(),
        UntypedDecimal value when decimal.Truncate(value.GetValue()) == value.GetValue() =>
            (long)Math.Clamp(value.GetValue(), long.MinValue, long.MaxValue),
        UntypedString text when long.TryParse(text.GetValue(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };

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
