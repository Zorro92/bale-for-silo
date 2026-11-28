using System.Globalization;
using Bale.Core.Api.Models;
using Bale.Core.Playback;
using Bale.Xbox.Services;
using Windows.Media.Playback;

namespace Bale.Xbox;

/// <summary>
/// The Stats tab's readout (Apple's tvOS panel, PlaybackStats.swift) for the Options panel, in every build. The debug
/// overlay (PlayerPage.Stats.cs) adds Bale's diagnostics on top of the same snapshot.
/// </summary>
public sealed partial class PlayerPage
{
    private PlaybackStats _hudStats = new();

    private static PlaybackStats PlanStats(Attempt? attempt, bool? isHls = null) => (attempt is { Plan: { } plan }
        // The title's versions name a version swap in the Diagnostics rows.
        ? PlaybackStats.FromPlan(plan, isHls, attempt.Request.Detail.Versions)
        : new PlaybackStats()) with
    {
        // The route is decided before the mount opens: name FFmpeg from the start, not after the first refresh.
        BackendDisplayName = attempt?.Media?.Route is { UseFfmpeg: true } ? "FFmpegInteropX" : "Windows MediaPlayer",
    };

    /// <summary>
    /// Reads the plan and the opened mount into <see cref="_hudStats"/>. The throughput meter wants one sample a
    /// second, so only the once-a-second refresh samples it; other refreshes keep the last estimate.
    /// </summary>
    private PlaybackStats RefreshHudStats(Attempt attempt, bool sampleBandwidth = true)
    {
        var stats = PlanStats(attempt, attempt.Media?.Transport.IsHls);
        var bandwidth = sampleBandwidth ? null : _hudStats.EstimatedBandwidthBps;
        if (attempt.Media is { } media)
        {
            if (media.FfmpegSource is not null)
                stats = ReadFfmpegStats(stats, media);
            else if (media is { Opened: true, Item: { } item })
            {
                stats = ReadVideoStats(stats, item, attempt.Plan, media.Transport.IsHls);
                stats = ReadAudioStats(stats, item);
            }
            if (sampleBandwidth)
            {
                try
                {
                    if (media.Stream is { } stream)
                        bandwidth = media.Throughput.Sample(stream.DownloadedBytes, stream.DownloadTime);
                    else if (media.Progressive is { } progressive)
                        bandwidth = media.Throughput.Sample(progressive.DownloadedBytes, progressive.DownloadTime);
                }
                catch (Exception ex) { LogFailure("stats bandwidth", ex); }
            }
            stats = stats with
            {
                Phase = PhaseOf(media),
                PlaybackRate = media.Player?.PlaybackSession.PlaybackRate,
                Subtitles = SubtitleValue(),
            };
            if (media.Player is { } player)
            {
                var smoothness = player.ReadSmoothness(stats.FrameRate);
                // The engine's own drop count. MediaPlayer does not measure drops (AGENTS.md, Player diagnostics).
                stats = stats with { Smoothness = smoothness, DroppedFrames = player is EngineBackend ? (long)smoothness.DroppedFrames : null };
            }
        }
        // Smoothness keeps renderer drops and waiting-time equivalents separate; audio underruns stay unknown.
        return _hudStats = stats with { EstimatedBandwidthBps = bandwidth };
    }

    /// <summary>The Stats tab's Playback status: a seek in progress, else the backend session's state (none while it is not open).</summary>
    private PlaybackStatsPhase? PhaseOf(Mount media)
    {
        if (_seeking) return PlaybackStatsPhase.Seeking;
        return media.Player?.PlaybackSession.PlaybackState switch
        {
            MediaPlaybackState.Opening => PlaybackStatsPhase.Loading,
            MediaPlaybackState.Buffering => PlaybackStatsPhase.Rebuffering,
            MediaPlaybackState.Playing => PlaybackStatsPhase.Playing,
            MediaPlaybackState.Paused => PlaybackStatsPhase.Paused,
            _ => null,
        };
    }

    /// <summary>Playback › Subtitles: the track the Subtitles tab selects, or Off; no value when the file has no subtitle tracks.</summary>
    private string? SubtitleValue() => _subtitleRows.Count == 0 ? null : _subtitleRows.FirstOrDefault(row => row.IsSelected)?.Name ?? "Off";

    /// <summary>
    /// The FFmpeg rows come from the mount's stream snapshot, never the wrapper: CurrentVideoStream/CurrentAudioStream
    /// take its source lock, which the sample thread may hold while it waits on a decoder (rav1g froze the UI thread
    /// there, 2026-11-18). Each refresh asks for a fresh read on the thread pool and shows the last completed one.
    /// </summary>
    private static PlaybackStats ReadFfmpegStats(PlaybackStats stats, Mount media)
    {
        stats = stats with
        {
            BackendDisplayName = "FFmpegInteropX",
            BackendRoute = media.Progressive is null ? "FFmpeg Direct" : media.Transport.IsHls ? "FFmpeg HLS" : "FFmpeg Progressive",
        };
        if (media.FfmpegStreams is not { } streams) return stats;
        if (media.Opened) streams.RequestRefresh();
        if (streams.Value?.Video is { } video)
            stats = stats with
            {
                VideoCodec = PlaybackStats.CompactValue(video.CodecName, 80),
                VideoDecoderName = FfmpegSource.DecoderName(video.DecoderEngine, media.Rav1gRoute),
                Resolution = video.PixelWidth > 0 && video.PixelHeight > 0
                    ? PlaybackStats.Dimensions(video.PixelWidth, video.PixelHeight) : null,
                FrameRate = video.FramesPerSecond > 0 && double.IsFinite(video.FramesPerSecond)
                    ? video.FramesPerSecond : stats.FrameRate,
            };
        if (streams.Value?.Audio is { } audio)
            stats = stats with
            {
                AudioCodec = PlaybackStats.CompactValue(audio.CodecName, 80),
                AudioDecoderName = FfmpegSource.DecoderName(audio.DecoderEngine),
                AudioChannelCount = audio.Channels,
            };
        return stats;
    }

    private static PlaybackStats ReadVideoStats(PlaybackStats stats, MediaPlaybackItem item, PlaybackPlan? plan, bool isHls)
    {
        try
        {
            var index = item.VideoTracks.SelectedIndex;
            if (index < 0 || index >= item.VideoTracks.Count) return stats;
            var track = item.VideoTracks[index];
            var format = track.GetEncodingProperties();
            stats = stats with
            {
                VideoCodec = format.Subtype,
                Resolution = format.Width > 0 && format.Height > 0
                    ? PlaybackStats.Dimensions((int)format.Width, (int)format.Height) : null,
                FrameRate = PlaybackStats.ResolveFrameRate(plan, isHls,
                    format.FrameRate.Numerator > 0 && format.FrameRate.Denominator > 0
                        ? (double)format.FrameRate.Numerator / format.FrameRate.Denominator : null),
            };
            stats = stats with { VideoDecoderName = $"Media Foundation ({track.SupportInfo.DecoderStatus})" };
        }
        catch (Exception ex) { LogFailure("stats video", ex); }
        return stats;
    }

    private static PlaybackStats ReadAudioStats(PlaybackStats stats, MediaPlaybackItem item)
    {
        try
        {
            var index = item.AudioTracks.SelectedIndex;
            if (index < 0 || index >= item.AudioTracks.Count) return stats;
            var track = item.AudioTracks[index];
            var format = track.GetEncodingProperties();
            stats = stats with
            {
                AudioCodec = format.Subtype,
                AudioChannelCount = format.ChannelCount,
            };
            stats = stats with { AudioDecoderName = $"Media Foundation ({track.SupportInfo.DecoderStatus})" };
        }
        catch (Exception ex) { LogFailure("stats audio", ex); }
        return stats;
    }
}
