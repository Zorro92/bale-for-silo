using Bale.Core.Api;
using Bale.Core.Api.Api.V2.Settings.Values.Item;
using Bale.Core.Api.Models;
using Bale.Core.Detail;
using Microsoft.Kiota.Abstractions.Serialization;

namespace Bale.Core.Settings;

/// <summary>
/// The profile's subtitle preferences as the server resolves them for this device (ProfileSettingsController.kt
/// Snapshot): what Settings › Subtitles shows and what Detail's and Up Next's Auto subtitle start from.
/// </summary>
/// <param name="Language">A language tag; "" for no preference (the manifest's null).</param>
/// <param name="Mode">Behavior; auto when unset or unknown.</param>
/// <param name="ShowForced">Show Forced Subtitles; on by default.</param>
/// <param name="Known">False until a read succeeded.</param>
public sealed record SubtitlePreferences(string Language = "", SubtitleMode Mode = SubtitleModes.Default, bool ShowForced = true,
    bool Known = false)
{
    /// <summary>
    /// The Auto preview's inputs (TvItemDetailViewModel.kt loadSubtitlePreferences): no preference is null there, since
    /// the preview reads "" as "no subtitles".
    /// </summary>
    public SubtitleAutoContext AutoContext() => new(Language.Length == 0 ? null : Language, SubtitleModes.Wire(Mode), ShowForced);

    /// <summary>
    /// What Detail and Up Next start Auto from: the canonical values the Settings pane writes (TvItemDetailViewModel.kt
    /// resolves them through the same ProfileSettingsController the settings screen writes with), and the profile
    /// record's fields only when that read failed, as Android falls back for a server that cannot resolve canonically.
    /// The profile record is resolved without this device's value and, on a server from before the profile fields were
    /// served from the canonical store, still shows the value before the last canonical write. Null when neither is known.
    /// </summary>
    public static SubtitleAutoContext? AutoContextFor(SubtitlePreferences? resolved, Profile? profile) =>
        resolved is { Known: true } preferences ? preferences.AutoContext()
        : profile is null ? null : new(profile.SubtitleLanguage, profile.SubtitleMode, profile.ShowForcedSubtitles ?? true);
}

/// <summary><c>playback.subtitle_appearance</c> as the server resolves it for this profile and device.</summary>
/// <param name="Value">The stored appearance merged over the contract default; the default before a read.</param>
/// <param name="DeviceOverride">This device keeps its own (scope profile_device): Only on This Device is on.</param>
/// <param name="Known">False until a read succeeded.</param>
/// <param name="TextOpacitySupported">The server's contract knows textOpacity (revision 14), so the row shows.</param>
public sealed record SubtitleAppearanceSetting(SubtitleAppearance Value, bool DeviceOverride = false, bool Known = false,
    bool TextOpacitySupported = true);

/// <summary>
/// Settings › Subtitles' values, read together from <c>/api/v2/settings/values/effective</c> (every key came with the
/// first settings manifest, so no server answers 404). Language, Behavior and Show Forced Subtitles are profile
/// preferences: a change writes scope=profile and re-resolves, keeping the optimistic value if only that read fails
/// (ProfileSettingsController.kt). The appearance writes the profile's value unless Only on This Device is on, which
/// keeps this device's own (AndroidPlayerSettingsStore.kt setSubtitleDeviceOverrideEnabled). Use Device Settings is a
/// device-local switch per server and profile (setSubtitleMatchesDevice, a local write).
/// </summary>
public sealed class SubtitleSettingsFeed(SiloApiClient api)
{
    public const string LanguageKey = SettingsLanguages.SubtitleLanguageKey;
    public const string ModeKey = SubtitleModes.Key;
    public const string ShowForcedKey = "playback.show_forced_subtitles";
    public const string AppearanceKey = SubtitleAppearance.Key;
    public const string DeviceScope = PlaybackSettingsFeed.DeviceScope;

    private static readonly string[] Keys = [LanguageKey, ModeKey, ShowForcedKey, AppearanceKey];

    private readonly object _sync = new();
    private readonly Func<string?>? _scope;
    private SubtitlePreferences _preferences = new();
    private IReadOnlyList<string> _suggestions = [];
    private SubtitleAppearanceSetting _appearance = new(SubtitleAppearance.Default);
    private int _generation;

    /// <param name="scope">The server and profile the values belong to (null while no profile is active).</param>
    public SubtitleSettingsFeed(SiloApiClient api, Func<string?>? scope) : this(api) => _scope = scope;

    /// <summary>Device-local storage for Use Device Settings; the playback feed's memory unless set (tests use their own).</summary>
    public ISettingsMemory? Memory { get; set; } = PlaybackSettingsFeed.DefaultMemory;

    public SubtitlePreferences Preferences { get { lock (_sync) return _preferences; } }

    /// <summary>The server's suggested_values for the Language picker (languages seen in the library).</summary>
    public IReadOnlyList<string> LanguageSuggestions { get { lock (_sync) return _suggestions; } }

    public SubtitleAppearanceSetting Appearance { get { lock (_sync) return _appearance; } }

    /// <summary>Use Device Settings for this server and profile: off unless this device turned it on.</summary>
    public bool MatchDevice
    {
        get
        {
            try { return MemoryKey() is { } key && Memory?.Read(key) == "1"; }
            catch (Exception) { return false; }
        }
    }

    /// <summary>Turns Use Device Settings on or off on this device; false when it could not be stored.</summary>
    public bool SetMatchDevice(bool on)
    {
        if (MemoryKey() is not { } key || Memory is not { } memory) return false;
        try
        {
            memory.Write(key, on ? "1" : "0");
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// What subtitles are drawn with: the console's caption settings over the stored appearance while Use Device Settings
    /// is on (AndroidPlayerSettingsStore.kt effectiveSubtitleAppearanceFlow), else the stored appearance.
    /// </summary>
    public SubtitleAppearance EffectiveAppearance(SystemCaptionStyle? system) =>
        MatchDevice && system is not null ? system.Apply(Appearance.Value) : Appearance.Value;

    public void Reset()
    {
        lock (_sync)
        {
            _generation++;
            _preferences = new();
            _suggestions = [];
            _appearance = new(SubtitleAppearance.Default);
        }
    }

    /// <summary>Reads every value; a failed read throws and keeps the last known ones.</summary>
    public async Task<SubtitlePreferences> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var generation = Generation();
        var collection = await api.Api.V2.Settings.Values.Effective.GetAsync(c => c.QueryParameters.Keys = Keys, cancellationToken)
            .ConfigureAwait(false);
        var items = collection?.Items ?? [];
        var language = items.FirstOrDefault(item => item.Key == LanguageKey);
        var tag = PlaybackSettingsFeed.Text(language?.Value)?.Trim();
        var preferences = new SubtitlePreferences(
            SettingsLanguages.IsPreservable(tag) ? tag! : string.Empty,
            SubtitleModes.FromWire(PlaybackSettingsFeed.Text(items.FirstOrDefault(item => item.Key == ModeKey)?.Value)) ?? SubtitleModes.Default,
            PlaybackSettingsFeed.Boolean(items.FirstOrDefault(item => item.Key == ShowForcedKey)?.Value) ?? true,
            true);
        var appearanceRow = items.FirstOrDefault(item => item.Key == AppearanceKey);
        var appearance = new SubtitleAppearanceSetting(SubtitleAppearance.FromWire(appearanceRow?.Value),
            appearanceRow?.Scope == DeviceScope, true, SubtitleAppearance.SupportsTextOpacity(collection?.Revision));
        lock (_sync)
        {
            if (generation == _generation)
            {
                _preferences = preferences;
                _suggestions = language?.SuggestedValues?.Where(value => !string.IsNullOrWhiteSpace(value)).ToList() ?? [];
                _appearance = appearance;
            }
            return _preferences;
        }
    }

    /// <summary>Detail's and Up Next's read: the resolved preferences, or null when the read failed.</summary>
    public async Task<SubtitlePreferences?> ReadPreferencesAsync(CancellationToken cancellationToken = default)
    {
        try { return await RefreshAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { return null; }
    }

    /// <summary>
    /// Language: a tag writes the profile's value; "" (None) clears it, the contract's spelling of no preference
    /// (ProfileSettingsController.kt writeLanguage: the server's language_tag validator refuses "").
    /// </summary>
    public async Task<SubtitlePreferences> SetLanguageAsync(string tag, CancellationToken cancellationToken = default)
    {
        var value = tag.Trim();
        var generation = Generation();
        if (value.Length == 0)
            await api.Api.V2.Settings.Values[LanguageKey].DeleteAsync(c => c.QueryParameters.Scope = DeleteScopeQueryParameterType.Profile,
                cancellationToken).ConfigureAwait(false);
        else await PutProfileAsync(LanguageKey, new UntypedString(value), cancellationToken).ConfigureAwait(false);
        Adopt(generation, preferences => preferences with { Language = value, Known = true });
        return await ReresolveAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<SubtitlePreferences> SetModeAsync(SubtitleMode mode, CancellationToken cancellationToken = default)
    {
        var generation = Generation();
        await PutProfileAsync(ModeKey, new UntypedString(SubtitleModes.Wire(mode)), cancellationToken).ConfigureAwait(false);
        Adopt(generation, preferences => preferences with { Mode = mode, Known = true });
        return await ReresolveAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<SubtitlePreferences> SetShowForcedAsync(bool on, CancellationToken cancellationToken = default)
    {
        var generation = Generation();
        await PutProfileAsync(ShowForcedKey, new UntypedBoolean(on), cancellationToken).ConfigureAwait(false);
        Adopt(generation, preferences => preferences with { ShowForced = on, Known = true });
        return await ReresolveAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// An appearance edit (or Reset Appearance, with the contract default) saves the whole object to the profile, or to
    /// this device while Only on This Device is on (AndroidPlayerSettingsStore.kt updateSubtitleAppearance: "An edit no
    /// longer turns on the device override"). The resolution order (device, profile, default) makes it the effective value.
    /// </summary>
    public async Task<SubtitleAppearanceSetting> SetAppearanceAsync(SubtitleAppearance next, CancellationToken cancellationToken = default)
    {
        var generation = Generation();
        var current = Appearance;
        var safe = next.Sanitized();
        await api.Api.V2.Settings.Values[AppearanceKey].PutAsync(new SettingValueWrite { Value = safe.ToWire(current.TextOpacitySupported) },
            c => c.QueryParameters.Scope = current.DeviceOverride ? PutScopeQueryParameterType.Profile_device : PutScopeQueryParameterType.Profile,
            cancellationToken).ConfigureAwait(false);
        return AdoptAppearance(generation, current with { Value = safe, Known = true });
    }

    /// <summary>
    /// Only on This Device: on copies the appearance shown to this device's own value; off deletes it, so the profile's
    /// applies again, and rereads (setSubtitleDeviceOverrideEnabled).
    /// </summary>
    public async Task<SubtitleAppearanceSetting> SetAppearanceOnlyOnThisDeviceAsync(bool on, CancellationToken cancellationToken = default)
    {
        var generation = Generation();
        var current = Appearance;
        if (on)
        {
            await api.Api.V2.Settings.Values[AppearanceKey].PutAsync(
                new SettingValueWrite { Value = current.Value.ToWire(current.TextOpacitySupported) },
                c => c.QueryParameters.Scope = PutScopeQueryParameterType.Profile_device, cancellationToken).ConfigureAwait(false);
            return AdoptAppearance(generation, current with { DeviceOverride = true, Known = true });
        }
        await api.Api.V2.Settings.Values[AppearanceKey].DeleteAsync(c => c.QueryParameters.Scope = DeleteScopeQueryParameterType.Profile_device,
            cancellationToken).ConfigureAwait(false);
        AdoptAppearance(generation, current with { DeviceOverride = false });
        await ReresolveAsync(cancellationToken).ConfigureAwait(false);
        return Appearance;
    }

    private Task PutProfileAsync(string key, UntypedNode value, CancellationToken cancellationToken) =>
        api.Api.V2.Settings.Values[key].PutAsync(new SettingValueWrite { Value = value },
            c => c.QueryParameters.Scope = PutScopeQueryParameterType.Profile, cancellationToken);

    /// <summary>
    /// ProfileSettingsController.kt reresolve: a stored value is not necessarily the effective one (a device row outranks
    /// the profile's), so the screen shows what the server resolves; if only that read fails, the write still landed and
    /// the optimistic value stays until the next read.
    /// </summary>
    private async Task<SubtitlePreferences> ReresolveAsync(CancellationToken cancellationToken)
    {
        try { await RefreshAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { }
        return Preferences;
    }

    private void Adopt(int generation, Func<SubtitlePreferences, SubtitlePreferences> change)
    {
        lock (_sync)
        {
            if (generation == _generation) _preferences = change(_preferences);
        }
    }

    private SubtitleAppearanceSetting AdoptAppearance(int generation, SubtitleAppearanceSetting setting)
    {
        lock (_sync)
        {
            if (generation == _generation) _appearance = setting;
            return setting;
        }
    }

    private int Generation() { lock (_sync) return _generation; }

    /// <summary>A local-settings key for this server and profile: a hash, so no address or id is stored in clear.</summary>
    private string? MemoryKey()
    {
        if (_scope?.Invoke() is not { Length: > 0 } scope) return null;
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(scope));
        return "subtitles_match_device." + Convert.ToHexString(hash, 0, 12).ToLowerInvariant();
    }
}
