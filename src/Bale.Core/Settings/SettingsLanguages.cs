using System.Globalization;
using System.Text.RegularExpressions;
using Bale.Core.Detail;

namespace Bale.Core.Settings;

/// <summary>One row of a settings picker: its id (what is stored) and its label.</summary>
public sealed record SettingsChoice(string Id, string Label);

/// <summary>
/// The language pickers' choices and labels (silo-android shared LanguageOptions.kt): the manifest's suggested language
/// floor, then the server's suggested values, then the current value, each language once; "no preference" leads with
/// the setting's own unset label. Labels are the languages' English names, as Bale's track labels are.
/// </summary>
public static partial class SettingsLanguages
{
    public const string SubtitleLanguageKey = "playback.subtitle_language";

    /// <summary>The picker id and stored spelling of "no preference" (the server stores it as no row; LanguageOptions.UNSET).</summary>
    public const string Unset = "";

    /// <summary>The manifest's unset_label for <c>playback.subtitle_language</c>.</summary>
    public const string SubtitleUnsetLabel = "None";

    /// <summary>Settings › Subtitles › Profile: the row and the picker title (settings_control_language).</summary>
    public const string SubtitleTitle = "Language";

    /// <summary>
    /// The manifest's <c>playback_subtitle_languages</c> set up to revision 7 (LanguageOptions.FLOOR_REVISION): revision 8's
    /// regional and script tags (zh-Hans, en-US, pt-BR, ...) are left out, as on Android, and stay selectable only as
    /// the current or a server-suggested value.
    /// </summary>
    public static IReadOnlyList<string> SubtitleFloor { get; } =
    [
        "ar", "bn", "bg", "zh", "hr", "cs", "da", "nl", "en", "fi", "fr", "de", "el", "he", "hi", "hu", "id", "it", "ja",
        "ko", "ms", "no", "fa", "pl", "pt", "ro", "ru", "sk", "sl", "es", "sv", "ta", "te", "th", "tr", "uk", "vi",
    ];

    /// <summary>LanguageOptions.options for the subtitle language: None, the floor, suggestions, then the current value.</summary>
    public static IReadOnlyList<SettingsChoice> SubtitleOptions(string? current, IEnumerable<string>? suggested) =>
        [new(Unset, SubtitleUnsetLabel), .. Named(SubtitleFloor, current, suggested)];

    /// <summary>LanguageOptions.label: the unset label for no preference or a value that is not a language tag.</summary>
    public static string SubtitleLabel(string? tag) =>
        string.IsNullOrWhiteSpace(tag) || !IsPreservable(tag) ? SubtitleUnsetLabel : Name(tag.Trim());

    /// <summary>
    /// LanguageOptions.namedOptions: each language once by its alias-aware identity. A suggestion never replaces a floor
    /// entry; the current value does, so an exact stored spelling (pt-BR, eng) stays the one that is checked.
    /// </summary>
    internal static IReadOnlyList<SettingsChoice> Named(IEnumerable<string> floor, string? current, IEnumerable<string>? suggested)
    {
        var values = new List<string>();
        var indexByLanguage = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var value in floor) Add(value, replaceAlias: false);
        foreach (var value in suggested ?? []) Add(value, replaceAlias: false);
        if (current is not null) Add(current, replaceAlias: true);
        return values.Select(value => new SettingsChoice(value, Name(value))).ToList();

        void Add(string raw, bool replaceAlias)
        {
            var value = raw.Trim();
            if (!IsPreservable(value)) return;
            var identity = Identity(value);
            if (indexByLanguage.TryGetValue(identity, out var existing))
            {
                if (replaceAlias) values[existing] = value;
                return;
            }
            indexByLanguage[identity] = values.Count;
            values.Add(value);
        }
    }

    /// <summary>LanguageOptions.isPreservableTag: a language tag, not "Off", "Default" or "und".</summary>
    public static bool IsPreservable(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.Equals("Off", StringComparison.OrdinalIgnoreCase)
        && !value.Equals("Default", StringComparison.OrdinalIgnoreCase)
        && LanguageTag().IsMatch(value)
        && PlaybackFormatting.SubtitleLanguageKey(value) is not null;

    /// <summary>
    /// LanguagePresentation.android.kt canonicalLanguageIdentity: lower case, '_' as '-', an ISO 639-2 primary folded to
    /// 639-1, and the script and region subtags kept, so "eng" and "en" are one language and "pt" and "pt-BR" two.
    /// </summary>
    internal static string Identity(string tag)
    {
        var parts = tag.Trim().ToLowerInvariant().Replace('_', '-').Split('-');
        parts[0] = PlaybackFormatting.SubtitleLanguageKey(parts[0]) ?? parts[0];
        return string.Join("-", parts);
    }

    /// <summary>The tag's English name ("Portuguese (Brazil)" for pt-BR); the upper-cased tag when it has none.</summary>
    internal static string Name(string tag)
    {
        var normalized = tag.Replace('_', '-');
        foreach (var candidate in new[] { normalized, Identity(normalized) })
        {
            try
            {
                var name = CultureInfo.GetCultureInfo(candidate).EnglishName;
                if (name.Length > 0 && !name.Equals(candidate, StringComparison.OrdinalIgnoreCase)
                    && !name.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase)) return name;
            }
            catch (CultureNotFoundException) { }
        }
        return normalized.ToUpperInvariant();
    }

    [GeneratedRegex("^[a-zA-Z]{2,3}([-_][a-zA-Z0-9]{1,8})*$")]
    private static partial Regex LanguageTag();
}
