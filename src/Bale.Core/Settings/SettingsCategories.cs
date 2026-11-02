namespace Bale.Core.Settings;

// screens/settings/TvSettingsScreen.kt: preserve the official order even when an entire
// unsupported pane is omitted. Playback/Subtitles wait for Bale's player; Diagnostics is separate.
public enum SettingsCategory { General, Playback, Subtitles, Diagnostics, Server }

public readonly record struct SettingsSupport(
    bool PlayerAvailable = false,
    bool DiagnosticsAvailable = false,
    bool DiagnosticsEligible = false);

public static class SettingsCategories
{
    public static IReadOnlyList<SettingsCategory> Ordered { get; } =
        [SettingsCategory.General, SettingsCategory.Playback, SettingsCategory.Subtitles, SettingsCategory.Diagnostics, SettingsCategory.Server];

    public static IReadOnlyList<SettingsCategory> Visible(SettingsSupport support) => Ordered.Where(category => category switch
    {
        SettingsCategory.Playback or SettingsCategory.Subtitles => support.PlayerAvailable,
        SettingsCategory.Diagnostics => support.DiagnosticsAvailable && support.DiagnosticsEligible,
        _ => true,
    }).ToList();

    // TvSettingsCategoryTest.kt: losing eligibility/support must re-home selection to General.
    public static SettingsCategory Resolve(SettingsCategory current, SettingsSupport support) =>
        Visible(support).Contains(current) ? current : SettingsCategory.General;
}
