using Bale.Core.Api.Models;

using Bale.Core.Playback;

// The C# originals, before the Rust port (docs/rust-port.md), for differential tests only.
namespace Bale.Core.Tests.Reference;


/// <summary>Pure per-mount policy. HLS never leaves AdaptiveMediaSource.</summary>
internal static class PlaybackRoutePolicy
{
    /// <param name="forceFfmpeg">Debug A/B only: demux every direct mount with FFmpeg, keeping system video decode.</param>
    public static PlaybackRouteDecision Decide(PlaybackPlan plan, bool isHls, bool ffmpegEnabled = true, bool forceFfmpeg = false,
        bool sdrOutput = false, IReadOnlyList<FileVersion>? versions = null, bool hlsThroughFfmpeg = false)
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
        var hls = isHls || string.Equals(plan.Stream?.Protocol, "hls", StringComparison.OrdinalIgnoreCase)
            || delivery == "hls" || delivery?.EndsWith("_hls", StringComparison.Ordinal) == true;
        var hlsFfmpeg = hls && hlsThroughFfmpeg;
        var ordinal = delivery == "server_remux_progressive" || hlsFfmpeg ? 0 : plan.SelectedTracks?.Audio?.Index;
        var direct = delivery is "original_http" or "progressive" or "server_remux_progressive" || hlsFfmpeg;
        var softwareVideo = SoftwareDecodePolicy.VideoCodecs.Contains(video!)
            && (original || !SoftwareDecodePolicy.OriginalOnlyVideoCodecs.Contains(video!));
        var softwareAudio = !SoftwareDecodePolicy.SystemAudioCodecs.Contains(audio!)
            && SoftwareDecodePolicy.AudioCodecs.Contains(audio!);
        // Windows' MKV source over-reads (~2.2x the bitrate) and blocks on the network; FFmpeg's demuxer does not.
        var matroska = IsMatroska(plan.Stream?.Container ?? plan.Source?.Container);
        var ffmpegContainer = SoftwareDecodePolicy.FfmpegOnlyContainers.Contains(
            (plan.Stream?.Container ?? plan.Source?.Container)?.Trim().ToLowerInvariant()!);
        // VP9/VC-1/MPEG-2 use verified compressed providers feeding the system decoder, regardless
        // of the container. Their claims do not imply Windows' own container source works.
        var demuxedVideo = SoftwareDecodePolicy.DemuxedVideoCodecs.Contains(video!);
        // A Dolby Vision profile 8 base-layer plan keeps the file's DV sample entries (dvh1/dvhe in MP4),
        // which Windows' own MP4 source is not trusted with; the FFmpeg demuxer hands plain HEVC on.
        var dv7 = DolbyVision7Policy.RequiresBaseLayer(plan);
        var dolbyVisionBaseLayer = dv7 || original && string.Equals(plan.DecisionReason,
            PlaybackProtocol.ClientDolbyVision8BaseLayerReason, StringComparison.OrdinalIgnoreCase);
        var toneMap = ConsoleToneMapPolicy.RequiresToneMap(plan, sdrOutput, versions, hlsFfmpeg);
        // A forward-only progressive remux has no length or ranges for Windows' own sources: it
        // always takes FFmpeg's non-seekable input (PlaybackProgressive), with the system decoder.
        var progressive = delivery == PlaybackProgressive.Delivery;
        var ffmpeg = ffmpegEnabled && direct && (!hls || hlsFfmpeg)
            && (progressive || hlsFfmpeg || softwareVideo || softwareAudio || matroska || ffmpegContainer || demuxedVideo || dolbyVisionBaseLayer || forceFfmpeg || toneMap);
        return new(ffmpeg, ffmpeg && softwareVideo, video, audio, ordinal, width, height, ffmpeg && dolbyVisionBaseLayer, toneMap, dv7);
    }

    /// <summary>MKV and WebM (Matroska-based); MP4 stays on Windows' own source.</summary>
    public static bool IsMatroska(string? container) => container?.Trim().ToLowerInvariant() is { } name
        && (name is "mkv" or "mka" or "webm" || name.Contains("matroska", StringComparison.Ordinal));

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
