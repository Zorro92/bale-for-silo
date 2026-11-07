using Bale.Core.Api.Models;

namespace Bale.Core.Playback;

public sealed record PlaybackRouteDecision(bool UseFfmpeg, bool SoftwareVideo, string? VideoCodec, string? AudioCodec, long? AudioOrdinal,
    long? VideoWidth, long? VideoHeight)
{
    public bool SoftwareAudio => UseFfmpeg && SoftwareDecodePolicy.AudioCodecs.Contains(AudioCodec!);
}

/// <summary>Pure per-mount policy. HLS never leaves AdaptiveMediaSource.</summary>
public static class PlaybackRoutePolicy
{
    public static PlaybackRouteDecision Decide(PlaybackPlan plan, bool isHls, bool ffmpegEnabled = true)
    {
        var delivery = plan.Delivery?.ToLowerInvariant();
        var original = delivery == "original_http";
        // SourceDescriptorFromFileV3 describes the server-selected audio track, not the file's default.
        // A remux's recipe describes its actual output, which has only the selected audio stream.
        var video = SoftwareDecodePolicy.NormalizeCodec(original ? plan.Source?.VideoCodec
            : plan.EffectiveRecipe?.VideoCodec ?? plan.Source?.VideoCodec);
        var width = original ? plan.Source?.Width : plan.EffectiveRecipe?.Width ?? plan.Source?.Width;
        var height = original ? plan.Source?.Height : plan.EffectiveRecipe?.Height ?? plan.Source?.Height;
        var audio = SoftwareDecodePolicy.NormalizeCodec(original ? plan.Source?.AudioCodec
            : plan.EffectiveRecipe?.AudioCodec ?? plan.Source?.AudioCodec);
        var ordinal = delivery == "server_remux_progressive" ? 0 : plan.SelectedTracks?.Audio?.Index;
        var direct = delivery is "original_http" or "progressive" or "server_remux_progressive";
        var hls = isHls || string.Equals(plan.Stream?.Protocol, "hls", StringComparison.OrdinalIgnoreCase)
            || delivery == "hls" || delivery?.EndsWith("_hls", StringComparison.Ordinal) == true;
        var softwareVideo = SoftwareDecodePolicy.VideoCodecs.Contains(video!);
        var softwareAudio = !SoftwareDecodePolicy.SystemAudioCodecs.Contains(audio!)
            && SoftwareDecodePolicy.AudioCodecs.Contains(audio!);
        var ffmpeg = ffmpegEnabled && direct && !hls && (softwareVideo || softwareAudio);
        return new(ffmpeg, ffmpeg && softwareVideo, video, audio, ordinal, width, height);
    }

    /// <summary>FFmpegInteropX moves its best stream first; Silo indexes audio in demux order.</summary>
    public static int AudioTrackIndex(long? selectedOrdinal, IReadOnlyList<int> streamIndices, int? expectedAudioCount = null)
    {
        if (expectedAudioCount is { } count && count != streamIndices.Count)
            throw new InvalidDataException("The planned audio inventory is incomplete.");
        if (selectedOrdinal is null && streamIndices.Count == 0) return -1;
        if (selectedOrdinal is not >= 0 || selectedOrdinal >= streamIndices.Count
            || streamIndices.Any(index => index < 0) || streamIndices.Distinct().Count() != streamIndices.Count)
            throw new InvalidDataException("The planned audio track is unavailable.");
        return streamIndices.Select((stream, index) => (stream, index)).OrderBy(pair => pair.stream)
            .ElementAt((int)selectedOrdinal.Value).index;
    }
}
