using System.Globalization;
using Bale.Core.Api.Models;

namespace Bale.Core.Playback;

public sealed record PlaybackInfoRow(string Label, string Value);

/// <summary>
/// The Options panel's Info tab (TvPlayerHud.kt HudInfoPane): HDR/resolution badges and the Stream rows
/// (how it plays, video, audio, subtitles, chapter) in names people know, read from what is playing:
/// the effective recipe when the server converts, else the source.
/// </summary>
public static class PlaybackOptionsInfo
{
    public static IReadOnlyList<string> Badges(PlaybackPlan plan)
    {
        var stats = PlaybackStats.FromPlan(plan);
        var badges = new List<string>();
        if (stats.HdrMode is { } hdr && hdr != "SDR") badges.Add(hdr);
        if (ResolutionName(plan.EffectiveRecipe?.Height ?? plan.Source?.Height) is { } resolution) badges.Add(resolution);
        return badges;
    }

    public static IReadOnlyList<PlaybackInfoRow> StreamRows(PlaybackPlan plan, string? subtitles, string? chapter)
    {
        var rows = new List<PlaybackInfoRow>();
        if (PlaybackStats.FromPlan(plan).Delivery is { } delivery) rows.Add(new("Playback", delivery));
        if (VideoCodecName(plan.EffectiveRecipe?.VideoCodec ?? plan.Source?.VideoCodec) is { } video) rows.Add(new("Video", video));
        var audioCodec = AudioCodecName(plan.EffectiveRecipe?.AudioCodec ?? plan.Source?.AudioCodec);
        var channels = ChannelName(plan.EffectiveRecipe?.AudioChannels ?? plan.Source?.AudioChannels);
        if (audioCodec is not null || channels is not null)
            rows.Add(new("Audio", string.Join(" · ", new[] { audioCodec, channels }.Where(part => part is not null))));
        rows.Add(new("Subtitles", string.IsNullOrWhiteSpace(subtitles) ? "Off" : subtitles));
        if (!string.IsNullOrWhiteSpace(chapter)) rows.Add(new("Chapter", chapter));
        return rows;
    }

    /// <summary>The chapter the position is in: its title, else "Chapter N"; null with no chapters.</summary>
    public static string? CurrentChapter(IReadOnlyList<VersionChapter> chapters, double position)
    {
        var ordered = chapters.Where(chapter => chapter.StartSeconds is { } start && double.IsFinite(start))
            .OrderBy(chapter => chapter.StartSeconds).ToList();
        var index = ordered.FindLastIndex(chapter => chapter.StartSeconds <= position + 0.5);
        if (index < 0) return null;
        return string.IsNullOrWhiteSpace(ordered[index].Title) ? $"Chapter {index + 1}" : ordered[index].Title!.Trim();
    }

    /// <summary>TvPlayerHud.kt videoCodecShortName: "H.264" rather than "avc1.640029".</summary>
    public static string? VideoCodecName(string? codec)
    {
        var id = codec?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(id)) return null;
        id = id[(id.LastIndexOf('/') + 1)..];
        return id switch
        {
            _ when id.StartsWith("avc", StringComparison.Ordinal) || id == "h264" => "H.264",
            _ when id.StartsWith("hev", StringComparison.Ordinal) || id.StartsWith("hvc", StringComparison.Ordinal) || id is "hevc" or "h265" => "HEVC",
            _ when id.StartsWith("dvh", StringComparison.Ordinal) || id.StartsWith("dva", StringComparison.Ordinal) => "Dolby Vision",
            _ when id.StartsWith("av01", StringComparison.Ordinal) || id == "av1" => "AV1",
            _ when id.StartsWith("vp09", StringComparison.Ordinal) || id == "vp9" => "VP9",
            _ when id.StartsWith("vp08", StringComparison.Ordinal) || id == "vp8" => "VP8",
            _ when id.StartsWith("mp4v", StringComparison.Ordinal) || id == "mpeg4" => "MPEG-4",
            "mpeg2" or "mpeg2video" => "MPEG-2",
            "vc1" => "VC-1",
            _ => id.Split('.')[0].ToUpperInvariant(),
        };
    }

    public static string? AudioCodecName(string? codec) => codec?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "eac3" or "ec-3" => "Dolby Digital Plus",
        "ac3" or "ac-3" => "Dolby Digital",
        "truehd" => "Dolby TrueHD",
        "dts" or "dca" => "DTS",
        "aac" or "mp4a" => "AAC",
        "flac" => "FLAC",
        "opus" => "Opus",
        "mp3" => "MP3",
        "vorbis" => "Vorbis",
        var other when other.StartsWith("pcm", StringComparison.Ordinal) => "PCM",
        var other => other.ToUpperInvariant(),
    };

    private static string? ChannelName(long? channels) => channels switch
    {
        null or <= 0 => null,
        1 => "Mono",
        2 => "Stereo",
        6 => "5.1",
        8 => "7.1",
        var count => count.Value.ToString(CultureInfo.InvariantCulture) + " ch",
    };

    private static string? ResolutionName(long? height) => height switch
    {
        null or <= 0 => null,
        >= 2000 => "4K",
        >= 1000 => "1080p",
        >= 700 => "720p",
        var h => h.Value.ToString(CultureInfo.InvariantCulture) + "p",
    };
}
