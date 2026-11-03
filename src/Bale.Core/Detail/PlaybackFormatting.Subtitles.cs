using Bale.Core.Api.Models;

namespace Bale.Core.Detail;

/// <summary>Inputs to the official subtitle Auto preview; absence of this context means resolution is unknown.</summary>
public sealed record SubtitleAutoContext(string? PreferredLanguage, string? Mode = null, bool ShowForced = false,
    string? AudioLanguage = null, WatchSubtitleSignature? TrackSignature = null);

public static partial class PlaybackFormatting
{
    /// <summary>AutoSubtitleResolver.kt's catalog-only preview, including remembered series choices.</summary>
    internal static int? AutoResolvedSubtitleOrdinal(IReadOnlyList<VersionSubtitleTrack> tracks, SubtitleAutoContext context)
    {
        if (tracks.Count == 0) return null;
        var mode = NonEmpty(context.Mode)?.ToLowerInvariant() ?? "auto";
        var audioLanguage = SubtitleLanguageKey(context.AudioLanguage);
        if (mode == "off")
        {
            return context.ShowForced && audioLanguage is not null ? BestForced(audioLanguage) : null;
        }

        var signature = context.TrackSignature;
        if (signature is not null)
        {
            for (var i = 0; i < tracks.Count; i++)
            {
                if (MatchesSignature(tracks[i], signature)) return i;
            }
        }

        if (context.PreferredLanguage is not null && string.IsNullOrWhiteSpace(context.PreferredLanguage)) return null;
        var targetLanguage = SubtitleLanguageKey(context.PreferredLanguage);
        if (targetLanguage is null) return mode == "always" ? Best(null, signature) : null;
        if (mode == "auto" && audioLanguage == targetLanguage)
        {
            return context.ShowForced ? BestForced(targetLanguage) : null;
        }

        return Best(targetLanguage, signature) ?? (context.ShowForced ? BestForced(null) : null);

        int? Best(string? language, WatchSubtitleSignature? remembered) =>
            Order(Enumerable.Range(0, tracks.Count).Where(i => language is null || SubtitleLanguageKey(tracks[i].Language) == language), remembered).FirstOrDefault();

        int? BestForced(string? language) =>
            Order(Enumerable.Range(0, tracks.Count).Where(i => tracks[i].Forced == true
                && (language is null || SubtitleLanguageKey(tracks[i].Language) == language)), null).FirstOrDefault();

        IEnumerable<int?> Order(IEnumerable<int> candidates, WatchSubtitleSignature? remembered) => candidates
            .OrderByDescending(i => remembered is null ? 0 : SignatureScore(tracks[i], remembered))
            .ThenBy(i => NeedsBurnIn(tracks[i]))
            .ThenBy(i => tracks[i].Forced == true)
            .ThenBy(i => IsHearingImpaired(tracks[i]))
            .ThenBy(i => tracks[i].External == true)
            .Select(i => (int?)i);
    }

    private static bool NeedsBurnIn(VersionSubtitleTrack track)
    {
        var token = string.Concat((track.Codec ?? string.Empty).Where(char.IsLetterOrDigit)).ToLowerInvariant();
        if (token.Contains("pgs")) return track.External == true;
        return token.Contains("vobsub") || token.Contains("dvdsubtitle") || token.Contains("dvbsub");
    }

    // The resolver honors the explicit flag too; the rendered badge uses the title only, as in TvPlaybackFormatting.kt.
    private static bool IsHearingImpaired(VersionSubtitleTrack track) => track.HearingImpaired == true || LabelIndicatesHearingImpaired(track.Title);

    private static bool MatchesSignature(VersionSubtitleTrack track, WatchSubtitleSignature signature) =>
        Normalize(signature.Source) == (track.External == true ? "external" : "embedded")
        && (Normalize(signature.Language) == Normalize(track.Language)
            || SubtitleLanguageKey(signature.Language) is { } language && language == SubtitleLanguageKey(track.Language))
        && Normalize(signature.Codec) == Normalize(track.Codec)
        && (Normalize(signature.Label).Length == 0 || Normalize(signature.Label) == Normalize(track.Title))
        && (signature.Forced == true) == (track.Forced == true)
        && (signature.HearingImpaired == true) == IsHearingImpaired(track);

    private static int SignatureScore(VersionSubtitleTrack track, WatchSubtitleSignature signature) =>
        (Normalize(signature.Source) == (track.External == true ? "external" : "embedded") ? 4 : 0)
        + ((signature.Forced == true) == (track.Forced == true) ? 2 : 0)
        + ((signature.HearingImpaired == true) == IsHearingImpaired(track) ? 2 : 0)
        + (Normalize(signature.Codec) == Normalize(track.Codec) ? 1 : 0)
        + (Normalize(signature.Label) == Normalize(track.Title) ? 1 : 0);

    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;

    internal static string? SubtitleLanguageKey(string? value)
    {
        var normalized = NonEmpty(value)?.ToLowerInvariant();
        if (normalized is null or "und") return null;
        var primary = normalized.Replace('_', '-').Split('-')[0];
        return SubtitleLanguageAliases.GetValueOrDefault(primary, primary);
    }

    // SubtitleLanguage.kt: both ISO 639-2 spellings must compare equal to their 639-1 language.
    private static readonly Dictionary<string, string> SubtitleLanguageAliases = new()
    {
        ["aar"] = "aa", ["abk"] = "ab", ["afr"] = "af", ["aka"] = "ak", ["alb"] = "sq", ["sqi"] = "sq",
        ["amh"] = "am", ["ara"] = "ar", ["arg"] = "an", ["arm"] = "hy", ["hye"] = "hy", ["asm"] = "as",
        ["ava"] = "av", ["ave"] = "ae", ["aym"] = "ay", ["aze"] = "az", ["bak"] = "ba", ["bam"] = "bm",
        ["baq"] = "eu", ["eus"] = "eu", ["bel"] = "be", ["ben"] = "bn", ["bih"] = "bh", ["bis"] = "bi",
        ["bos"] = "bs", ["bre"] = "br", ["bul"] = "bg", ["bur"] = "my", ["mya"] = "my", ["cat"] = "ca",
        ["cha"] = "ch", ["che"] = "ce", ["chi"] = "zh", ["zho"] = "zh", ["chu"] = "cu", ["chv"] = "cv",
        ["cor"] = "kw", ["cos"] = "co", ["cre"] = "cr", ["cze"] = "cs", ["ces"] = "cs", ["dan"] = "da",
        ["div"] = "dv", ["dut"] = "nl", ["nld"] = "nl", ["dzo"] = "dz", ["eng"] = "en", ["epo"] = "eo",
        ["est"] = "et", ["ewe"] = "ee", ["fao"] = "fo", ["fij"] = "fj", ["fin"] = "fi", ["fre"] = "fr",
        ["fra"] = "fr", ["fry"] = "fy", ["ful"] = "ff", ["geo"] = "ka", ["kat"] = "ka", ["ger"] = "de",
        ["deu"] = "de", ["gla"] = "gd", ["gle"] = "ga", ["glg"] = "gl", ["glv"] = "gv", ["gre"] = "el",
        ["ell"] = "el", ["grn"] = "gn", ["guj"] = "gu", ["hat"] = "ht", ["hau"] = "ha", ["heb"] = "he",
        ["her"] = "hz", ["hin"] = "hi", ["hmo"] = "ho", ["hrv"] = "hr", ["hun"] = "hu", ["ibo"] = "ig",
        ["ice"] = "is", ["isl"] = "is", ["ido"] = "io", ["iii"] = "ii", ["iku"] = "iu", ["ile"] = "ie",
        ["ina"] = "ia", ["ind"] = "id", ["ipk"] = "ik", ["ita"] = "it", ["jav"] = "jv", ["jpn"] = "ja",
        ["kal"] = "kl", ["kan"] = "kn", ["kas"] = "ks", ["kau"] = "kr", ["kaz"] = "kk", ["khm"] = "km",
        ["kik"] = "ki", ["kin"] = "rw", ["kir"] = "ky", ["kom"] = "kv", ["kon"] = "kg", ["kor"] = "ko",
        ["kua"] = "kj", ["kur"] = "ku", ["lao"] = "lo", ["lat"] = "la", ["lav"] = "lv", ["lim"] = "li",
        ["lin"] = "ln", ["lit"] = "lt", ["ltz"] = "lb", ["lub"] = "lu", ["lug"] = "lg", ["mac"] = "mk",
        ["mkd"] = "mk", ["mah"] = "mh", ["mal"] = "ml", ["mao"] = "mi", ["mri"] = "mi", ["mar"] = "mr",
        ["may"] = "ms", ["msa"] = "ms", ["mlg"] = "mg", ["mlt"] = "mt", ["mon"] = "mn", ["nau"] = "na",
        ["nav"] = "nv", ["nbl"] = "nr", ["nde"] = "nd", ["ndo"] = "ng", ["nep"] = "ne", ["nno"] = "nn",
        ["nob"] = "nb", ["nor"] = "no", ["nya"] = "ny", ["oci"] = "oc", ["oji"] = "oj", ["ori"] = "or",
        ["orm"] = "om", ["oss"] = "os", ["pan"] = "pa", ["per"] = "fa", ["fas"] = "fa", ["pli"] = "pi",
        ["pol"] = "pl", ["por"] = "pt", ["pus"] = "ps", ["que"] = "qu", ["roh"] = "rm", ["rum"] = "ro",
        ["ron"] = "ro", ["run"] = "rn", ["rus"] = "ru", ["sag"] = "sg", ["san"] = "sa", ["sin"] = "si",
        ["slo"] = "sk", ["slk"] = "sk", ["slv"] = "sl", ["sme"] = "se", ["smo"] = "sm", ["sna"] = "sn",
        ["snd"] = "sd", ["som"] = "so", ["sot"] = "st", ["spa"] = "es", ["srd"] = "sc", ["srp"] = "sr",
        ["ssw"] = "ss", ["sun"] = "su", ["swa"] = "sw", ["swe"] = "sv", ["tah"] = "ty", ["tam"] = "ta",
        ["tat"] = "tt", ["tel"] = "te", ["tgk"] = "tg", ["tgl"] = "tl", ["tha"] = "th", ["tib"] = "bo",
        ["bod"] = "bo", ["tir"] = "ti", ["ton"] = "to", ["tsn"] = "tn", ["tso"] = "ts", ["tuk"] = "tk",
        ["tur"] = "tr", ["twi"] = "tw", ["uig"] = "ug", ["ukr"] = "uk", ["urd"] = "ur", ["uzb"] = "uz",
        ["ven"] = "ve", ["vie"] = "vi", ["vol"] = "vo", ["wel"] = "cy", ["cym"] = "cy", ["wln"] = "wa",
        ["wol"] = "wo", ["xho"] = "xh", ["yid"] = "yi", ["yor"] = "yo", ["zha"] = "za", ["zul"] = "zu",
    };
}
