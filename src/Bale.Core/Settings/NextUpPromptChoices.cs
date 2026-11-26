using System.Globalization;
using Bale.Core.Playback;

namespace Bale.Core.Settings;

/// <summary>
/// Settings › Playback › Episodes › Show Next Up (TvSettingsScreen.kt:1395, PlaybackPicker.NextUpPrompt):
/// <c>playback.next_up_prompt_seconds</c>, how long before the end of an episode Up Next opens (manifest: integer
/// 0–120 s, default 30, profile or profile_device). The player reads it from <see cref="PlaybackSettingsFeed"/>.
/// </summary>
public static class NextUpPromptChoices
{
    /// <summary>settings_nextUp_title: the row and the picker title.</summary>
    public const string Title = "Show Next Up";

    /// <summary>TvSettingsScreen.kt NextUpPromptOptions ("Mirrors tvOS"): seconds before the end, 0 = at the end.</summary>
    public static IReadOnlyList<int> Seconds { get; } = [0, 10, 30, 60, 120];

    public const int Default = NextUpPrompt.DefaultSeconds;

    /// <summary>
    /// nextUpPromptLabel: settings_nextUp_atEnd, the settings_nextUp_secondsBeforeEnd plural below a minute, else whole
    /// minutes (settings_nextUp_minutesBeforeEnd; 90 s reads "1 minute before end", as on Android).
    /// </summary>
    public static string Label(int seconds) => seconds switch
    {
        <= 0 => "At end",
        < 60 => Count(seconds, "second") + " before end",
        _ => Count(seconds / 60, "minute") + " before end",
    };

    /// <summary>The picker id of "Use profile setting" (TvSettingsScreen.kt UseProfileSettingId; never on the wire).</summary>
    public const string UseProfileId = "profile";

    /// <summary>A choice's picker id: its seconds, invariant.</summary>
    public static string Id(int seconds) => seconds.ToString(CultureInfo.InvariantCulture);

    /// <summary>The seconds a picker id names; null for "Use profile setting" or anything that is not a choice.</summary>
    public static int? FromId(string? id) =>
        int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && Seconds.Contains(seconds) ? seconds : null;

    /// <summary>
    /// profileLayered: "Use profile setting" is the checked choice while this device has no value of its own; else the
    /// device's lead when it is a choice. A device value set elsewhere that is not one (the web's slider) checks none,
    /// and the sheet then focuses its first row (TvSettingsPickerSheet's selectedIndex.coerceAtLeast(0)).
    /// </summary>
    public static string? SelectedId(IntegerSetting setting) =>
        !setting.DeviceOverride ? UseProfileId : Seconds.Contains(setting.Value) ? Id(setting.Value) : null;

    private static string Count(int count, string unit) =>
        count.ToString(CultureInfo.InvariantCulture) + " " + unit + (count == 1 ? string.Empty : "s");
}
