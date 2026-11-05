#if DEBUG_SERVER
using System.Diagnostics;
using System.Globalization;
using Bale.Core.Api.Models;
using Bale.Core.Playback;
using Bale.Xbox.Services;
using Windows.Media.Playback;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Bale.Xbox;

public sealed partial class PlayerPage
{
    private IReadOnlyList<(string Label, string Value)> _statsRows = [];

    internal IReadOnlyList<(string Label, string Value)> DebugStatsRows => _statsRows;
    internal bool DebugStatsVisible => StatsPanel.Visibility == Visibility.Visible;
    internal string DebugHdrOutput => DebugHdrPlayback.Output(_attempt?.PretendHdr == true, _attempt?.Plan);

    internal void ToggleDebugStats()
    {
        try
        {
            StatsPanel.Visibility = DebugStatsVisible ? Visibility.Collapsed : Visibility.Visible;
            if (DebugStatsVisible) RenderStatsRows();
        }
        catch (Exception ex) { LogFailure("stats toggle", ex); }
    }

    private static PlaybackStats PlanStats(PlaybackPlan? plan, bool? isHls = null) => (plan is null ? new PlaybackStats() : PlaybackStats.FromPlan(plan, isHls)) with
    {
        BackendDisplayName = "Windows MediaPlayer",
        // VideoBackendCapabilities.kt reports backend support, not a claimed container list.
        HardContainers = "No",
    };

    private void ResetStats(PlaybackPlan? plan = null)
    {
        try { SetStats(PlanStats(plan)); }
        catch (Exception ex) { LogFailure("stats reset", ex); }
    }

    private void RefreshStats(Attempt attempt)
    {
        try
        {
            var stats = PlanStats(attempt.Plan, attempt.Media?.Transport.IsHls);
            string? processCpu = null;
            PlaybackFrameDeliverySnapshot? frames = null;
            if (attempt.Media is { } media)
            {
                processCpu = ReadProcessCpu(media);
                frames = media.FrameDelivery?.Read();
                if (media.FfmpegSource is not null)
                {
                    stats = ReadFfmpegStats(stats, media);
                }
                else if (media is { Opened: true, Item: { } item })
                {
                    stats = ReadVideoStats(stats, item, attempt.Plan, media.Transport.IsHls);
                    stats = ReadAudioStats(stats, item);
                }
                long? bandwidth = null;
                try
                {
                    if (media.Stream is { } stream)
                        bandwidth = media.Throughput.Sample(stream.DownloadedBytes, stream.DownloadTime);
                    else if (media.Adaptive is { } adaptive && adaptive.InboundBitsPerSecond is > 0 and <= long.MaxValue and var inbound)
                        bandwidth = (long)inbound;
                }
                catch (Exception ex) { LogFailure("stats bandwidth", ex); }
                var bufferingTicks = media.BufferingTicks;
                if (media.BufferingSince is { } started) bufferingTicks += Math.Max(0, Stopwatch.GetTimestamp() - started);
                stats = stats with
                {
                    EstimatedBandwidthBps = bandwidth,
                    BufferingCount = media.BufferingCount,
                    BufferingSeconds = bufferingTicks / (double)Stopwatch.Frequency,
                    // Buffering is not evidence of dropped frames or audio underruns.
                };
            }
            SetStats(stats, processCpu, frames);
        }
        catch (Exception ex) { LogFailure("stats refresh", ex); }
    }

    private static PlaybackStats ReadFfmpegStats(PlaybackStats stats, Mount media)
    {
        stats = stats with { BackendDisplayName = "FFmpegInteropX", BackendRoute = "FFmpeg Direct" };
        try
        {
            if (media.FfmpegSource!.CurrentVideoStream is { } video)
                stats = stats with
                {
                    VideoCodec = DebugScreenText.Compact(video.CodecName, 80),
                    VideoDecoderName = FfmpegSource.DecoderName(video.DecoderEngine),
                    Resolution = video.PixelWidth > 0 && video.PixelHeight > 0
                        ? string.Create(CultureInfo.InvariantCulture, $"{video.PixelWidth}x{video.PixelHeight}") : null,
                    FrameRate = video.FramesPerSecond > 0 ? video.FramesPerSecond : null,
                };
        }
        catch (Exception ex) { LogFailure("stats FFmpeg video", ex); }
        try
        {
            if (media.FfmpegSource!.CurrentAudioStream is { } audio)
                stats = stats with
                {
                    AudioCodec = DebugScreenText.Compact(audio.CodecName, 80),
                    AudioDecoderName = FfmpegSource.DecoderName(audio.DecoderEngine),
                    AudioChannelCount = audio.Channels,
                    AudioSampleRate = audio.SampleRate,
                };
        }
        catch (Exception ex) { LogFailure("stats FFmpeg audio", ex); }
        return stats;
    }

    private static string ReadProcessCpu(Mount media)
    {
        var now = Stopwatch.GetTimestamp();
        var value = DebugProcessCpu.Read(out var failure);
        if (failure is not null && failure != media.LastCpuFailure) Log.Info("player: " + failure);
        media.LastCpuFailure = failure;
        var wall = media.CpuTimestamp is { } previous ? Stopwatch.GetElapsedTime(previous, now) : TimeSpan.Zero;
        var usage = DebugFfmpegPlaytest.CpuUsage(media.CpuTime, value, wall, Environment.ProcessorCount);
        media.CpuTime = value;
        media.CpuTimestamp = now;
        return failure is null ? usage : string.Create(CultureInfo.InvariantCulture, $"{failure} · {Environment.ProcessorCount} CPUs");
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
                    ? string.Create(CultureInfo.InvariantCulture, $"{format.Width}x{format.Height}") : null,
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
                AudioSampleRate = format.SampleRate,
            };
            stats = stats with { AudioDecoderName = $"Media Foundation ({track.SupportInfo.DecoderStatus})" };
        }
        catch (Exception ex) { LogFailure("stats audio", ex); }
        return stats;
    }

    private void OnStatsBufferingStarted(MediaPlaybackSession sender, object args) => RecordBuffering(sender, starting: true);
    private void OnStatsBufferingEnded(MediaPlaybackSession sender, object args) => RecordBuffering(sender, starting: false);

    private void RecordBuffering(MediaPlaybackSession sender, bool starting)
    {
        // Capture event time before dispatch so a busy UI does not inflate the buffering duration.
        var now = Stopwatch.GetTimestamp();
        _ = DispatchAsync(() =>
        {
            if (_attempt is not { Media: { Player: { } player } media } attempt
                || !ReferenceEquals(player.PlaybackSession, sender) || !IsCurrent(attempt, media) || media.FailureQueued) return;
            if (starting && media.BufferingSince is null)
            {
                media.BufferingCount++;
                media.BufferingSince = now;
            }
            else if (!starting && media.BufferingSince is { } started)
            {
                media.BufferingTicks += Math.Max(0, now - started);
                media.BufferingSince = null;
            }
        });
    }

    private void SetStats(PlaybackStats stats, string? processCpu = null, PlaybackFrameDeliverySnapshot? frames = null)
    {
        var rows = stats.HudRows();
        rows = [.. rows, ("HDR output", DebugHdrOutput),
            ("Late frames", frames is null ? "unavailable" : string.Create(CultureInfo.InvariantCulture,
                $"{frames.LateFrames} · {frames.RecentLateFrames}/10s ({frames.RecentLateFramesPerSecond:0.0}/s)")),
            ("Delivered ahead", frames?.DeliveredAheadSeconds is { } ahead
                ? ahead.ToString("0.000", CultureInfo.InvariantCulture) + " s" : "unavailable"),
            // Neither MediaPlayer's WinRT API nor the pinned FFmpeg wrapper exposes render drops.
            ("Dropped frames", "unavailable")];
        // Debug-only process load is a Bale diagnostic, after the official HUD rows.
        if (processCpu is not null) rows = [.. rows, ("CPU", processCpu)];
        if (_statsRows.SequenceEqual(rows)) return;
        _statsRows = rows;
        if (DebugStatsVisible) RenderStatsRows();
    }

    private void RenderStatsRows()
    {
        StatsLeftRows.Children.Clear();
        StatsRightRows.Children.Clear();
        // TvPlayerHud.kt HudStatsPane: fill top-to-bottom, then across, with the extra row on the left.
        var split = (_statsRows.Count + 1) / 2;
        for (var i = 0; i < _statsRows.Count; i++)
        {
            var (label, value) = _statsRows[i];
            var row = new Grid { Style = StatsRowStyleSource.Style };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock { Text = label, Style = StatsLabelStyleSource.Style });
            var text = new TextBlock { Text = value, Style = StatsValueStyleSource.Style };
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            (i < split ? StatsLeftRows : StatsRightRows).Children.Add(row);
        }
    }
}
#endif
