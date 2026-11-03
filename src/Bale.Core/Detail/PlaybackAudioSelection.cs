using Bale.Core.Api.Models;

namespace Bale.Core.Detail;

/// <summary>The audio fields of shared/src/commonMain/kotlin/org/siloserver/silo/model/playback/PlaybackModels.kt's ClientCodecCapabilities.</summary>
public sealed record ClientAudioCapabilities(IReadOnlyList<string> DecodeCodecs, AudioPassthroughCapabilities? Passthrough = null);

public sealed record AudioPassthroughEntry(string Codec, IReadOnlyList<int> ChannelCounts);

public sealed record AudioPassthroughCapabilities(IReadOnlyList<string> Codecs, int MaxChannels = 2,
    IReadOnlyList<AudioPassthroughEntry>? Entries = null);

/// <summary>Catalog-only TV Auto policy; explicit per-play picks remain the caller's responsibility.</summary>
public static class PlaybackAudioSelection
{
    /// <summary>android-shared/src/androidMain/kotlin/org/siloserver/silo/common/player/TrackSelectionPresets.kt.</summary>
    public static string? EffectivePreferredAudioLanguage(string? settingsLanguage, string? profileLanguage) =>
        NonBlank(settingsLanguage) ?? NonBlank(profileLanguage);

    /// <summary>
    /// androidTvApp/src/androidMain/kotlin/org/siloserver/silo/tv/ui/screens/detail/TvItemDetailViewModel.kt:
    /// a series choice (including an unsent pick/reset) outranks the device's language/capability policy.
    /// </summary>
    public static int? AutomaticAudioOrdinal(FileVersion? version, string? preferredAudioLanguage,
        ClientAudioCapabilities? capabilities, PendingSeriesAudioChoice? pendingChoice = null)
    {
        if (version is null) return null;
        var tracks = version.AudioTracks ?? [];
        var effective = pendingChoice is null ? version.EffectiveAudioTrackIndex
            : pendingChoice.Choice is { } choice ? PlaybackTrackChoices.ResolveAudioOrdinal(choice, tracks) : null;
        if (effective is >= 0 && effective < tracks.Count) return (int)effective;
        return ResolveAutomaticAudioTrackOrdinal(version, preferredAudioLanguage, capabilities);
    }

    /// <summary>androidTvApp/src/androidMain/kotlin/org/siloserver/silo/tv/ui/screens/detail/TvPlaybackFormatting.kt.</summary>
    public static int? ResolveAutomaticAudioTrackOrdinal(FileVersion? version, string? preferredAudioLanguage,
        ClientAudioCapabilities? capabilities) => version is null || capabilities is null ? null
        : SelectBestCompatibleAudioTrackOrdinal(version.AudioTracks ?? [], preferredAudioLanguage, capabilities);

    /// <summary>
    /// android-shared/src/androidMain/kotlin/org/siloserver/silo/common/player/TrackSelectionPresets.kt:
    /// language first (preferred, then English), main mix before commentary, then compatible quality.
    /// If that language needs adaptation it still wins over another language's directly playable track.
    /// </summary>
    public static int? SelectBestCompatibleAudioTrackOrdinal(IReadOnlyList<AudioTrack> tracks,
        string? preferredAudioLanguage, ClientAudioCapabilities capabilities)
    {
        var candidates = Enumerable.Range(0, tracks.Count).ToList();
        var languages = new[] { PlaybackFormatting.SubtitleLanguageKey(preferredAudioLanguage), "en" }
            .OfType<string>().Distinct();
        foreach (var language in languages)
        {
            var tier = MainMixes(candidates.Where(i => PlaybackFormatting.SubtitleLanguageKey(tracks[i].Language) == language));
            if (tier.Count > 0) return Best(tier.Where(i => IsDirectlyCompatible(tracks[i], capabilities))) ?? Best(tier);
        }

        var mainMixes = MainMixes(candidates);
        var compatible = mainMixes.Where(i => IsDirectlyCompatible(tracks[i], capabilities)).ToList();
        return Best(compatible.Where(i => tracks[i].Default == true)) ?? Best(compatible)
            ?? Best(mainMixes.Where(i => tracks[i].Default == true)) ?? Best(mainMixes);

        List<int> MainMixes(IEnumerable<int> pool)
        {
            var all = pool.ToList();
            var main = all.Where(i => !LooksLikeCommentary(tracks[i].Title)).ToList();
            return main.Count > 0 ? main : all;
        }

        int? Best(IEnumerable<int> pool) => pool.OrderByDescending(i => QualityRank(tracks[i].Codec))
            .ThenByDescending(i => tracks[i].Channels ?? 0).ThenByDescending(i => tracks[i].Bitrate ?? 0)
            .ThenByDescending(i => tracks[i].Default == true).ThenBy(i => i).Select(i => (int?)i).FirstOrDefault();
    }

    private static bool IsDirectlyCompatible(AudioTrack track, ClientAudioCapabilities capabilities)
    {
        var codec = PreferenceCodec(track.Codec);
        if (codec is null) return false;
        // TrackSelectionPresets.kt treats AAC as the guaranteed decoder. Decode support is independent
        // of the sink's encoded channel limits: a native decoder can output/downmix to PCM instead.
        if (codec == "aac" || capabilities.DecodeCodecs.Any(value => PreferenceCodec(value) == codec)) return true;
        if (capabilities.Passthrough is not { } passthrough) return false;
        var candidates = codec switch
        {
            "eac3_joc" => new[] { "eac3_joc", "eac3" },
            "dts_hd" => new[] { "dts_hd", "dts" },
            _ => new[] { codec },
        };
        return candidates.Any(candidate => SinkCanPassthrough(candidate, track.Channels ?? 0, passthrough));
    }

    private static bool SinkCanPassthrough(string codec, long channels, AudioPassthroughCapabilities capabilities)
    {
        if (!capabilities.Codecs.Contains(codec)) return false;
        if (channels <= 0) return true;
        var counts = capabilities.Entries?.FirstOrDefault(entry => entry.Codec == codec)?.ChannelCounts;
        if (counts is not { Count: > 0 }) return channels <= capabilities.MaxChannels;
        if (counts.Any(count => count == channels)) return true;
        // android-shared/src/androidMain/kotlin/org/siloserver/silo/common/player/PlaybackCapabilityDetector.kt:
        // only 2/6/8 were probed; an absent one is refused, other counts use THIS codec's maximum.
        return channels is not (2 or 6 or 8) && channels <= counts.Max();
    }

    private static string? PreferenceCodec(string? raw)
    {
        var token = string.Concat((raw?.Split('/')[^1] ?? string.Empty).ToLowerInvariant().Where(char.IsLetterOrDigit));
        // TrackSelectionPresets.kt's audioPreferenceCodec, then video/MountedAudioMatching.kt's
        // canonicalAudioCodecFamily; preserve JOC/DTS-HD quality tiers before folding codec families.
        return token switch
        {
            "" => null,
            var value when value.Contains("truehd") || value == "mlp" => "truehd",
            var value when value.Contains("eac3joc") || value.Contains("ec3joc") => "eac3_joc",
            var value when value.Contains("dtshd") || value.Contains("dtsma") || value.Contains("dtshra") => "dts_hd",
            var value when value.StartsWith("ec3", StringComparison.Ordinal) || value.StartsWith("eac3", StringComparison.Ordinal) || value == "ddp" => "eac3",
            var value when value.StartsWith("ac3", StringComparison.Ordinal) => "ac3",
            var value when value.StartsWith("ac4", StringComparison.Ordinal) => "ac4",
            var value when value.StartsWith("mp4a", StringComparison.Ordinal) || value is "aac" or "aacl" => "aac",
            var value when value.StartsWith("vnddts", StringComparison.Ordinal) || value.StartsWith("dts", StringComparison.Ordinal) || value == "dca" => "dts",
            var value when value.StartsWith("flac", StringComparison.Ordinal) => "flac",
            var value when value.StartsWith("opus", StringComparison.Ordinal) => "opus",
            var value when value.StartsWith("vorbis", StringComparison.Ordinal) => "vorbis",
            var value when value.StartsWith("mpeg", StringComparison.Ordinal) || value is "mp3" or "mp2" => "mp3",
            var value when value.StartsWith("pcm", StringComparison.Ordinal) || value.StartsWith("raw", StringComparison.Ordinal) => "pcm",
            _ => token,
        };
    }

    private static int QualityRank(string? codec) => PreferenceCodec(codec) switch
    {
        "truehd" => 1000, "eac3_joc" => 950, "eac3" => 900, "dts_hd" => 850, "flac" => 825,
        "ac4" => 800, "dts" => 700, "ac3" => 650, "aac" => 500, "opus" => 450,
        "vorbis" => 400, "mp3" => 300, "pcm" => 250, _ => 0,
    };

    private static bool LooksLikeCommentary(string? title) => title is not null
        && new[] { "commentary", "description", "descriptive", "director", "isolated score" }
            .Any(token => title.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
