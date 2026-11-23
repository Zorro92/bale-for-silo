#if DEBUG_SERVER
using System.Diagnostics;
using System.Globalization;
using Bale.Core.Api.Models;
using Bale.Core.Playback;
using Bale.Xbox.Services;
using Windows.Media.Playback;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Bale.Xbox;

public sealed partial class PlayerPage
{
    private IReadOnlyList<(string Label, string Value)> _statsRows = [];

    internal IReadOnlyList<(string Label, string Value)> DebugStatsRows
    {
        get
        {
            // stats and where must see the post-start engine, even between timer ticks.
            if (_attempt is { } attempt) RefreshStats(attempt);
            return _statsRows;
        }
    }
    internal bool DebugStatsVisible => StatsPanel.Visibility == Visibility.Visible;
    internal string DebugHdrOutput => DebugHdrPlayback.Output(_attempt?.PretendHdr == true, _attempt?.Plan,
        _attempt is { Media: { } media } attempt && attempt.FfmpegTuning.RequiresToneMapGuard(media.Route));
    internal string DebugLateFrameTrace => _attempt?.Media?.FrameDelivery?.DescribeLateFrameTrace()
        ?? "frame trace unavailable (no software video observer)\n";

    /// <summary>The playing original's transport and length, for `bale-debug.sh fetchbench`; null without a range stream.</summary>
    internal (Bale.Core.Playback.PlaybackMedia Media, long Length)? DebugRangeMedia =>
        _attempt?.Media is { Stream: { } stream } media ? (media.Transport, stream.Length) : null;

    internal void ResetDebugLateFrameTrace() => _attempt?.Media?.FrameDelivery?.ResetLateFrameTrace();

    internal void ToggleDebugStats()
    {
        try
        {
            StatsPanel.Visibility = DebugStatsVisible ? Visibility.Collapsed : Visibility.Visible;
            if (DebugStatsVisible)
            {
                RenderStatsRows();
                StatsScroller.ChangeView(null, 0, null, disableAnimation: true);
            }
        }
        catch (Exception ex) { LogFailure("stats toggle", ex); }
    }

    internal void RefreshDebugStatsRows()
    {
        if (DebugStatsVisible) RenderStatsRows();
    }

    private void ResetStats(Attempt? attempt = null)
    {
        try { SetStats(PlanStats(attempt)); }
        catch (Exception ex) { LogFailure("stats reset", ex); }
    }

    private void RefreshStats(Attempt attempt)
    {
        try
        {
            // The official rows (and the throughput sample) come from the Stats tab's reader.
            var stats = RefreshHudStats(attempt);
            string? processCpu = null;
            PlaybackFrameDeliverySnapshot? frames = null;
            if (attempt.Media is { } media)
            {
                processCpu = ReadProcessCpu(media);
                frames = media.FrameDelivery?.Read();
                if (media.FfmpegSource is not null) media.MssVideoMediaType?.Refresh(media.Started);
                var bufferingTicks = media.BufferingTicks;
                if (media.BufferingSince is { } started) bufferingTicks += Math.Max(0, Stopwatch.GetTimestamp() - started);
                stats = stats with
                {
                    BufferingCount = media.BufferingCount,
                    BufferingSeconds = bufferingTicks / (double)Stopwatch.Frequency,
                };
            }
            SetStats(stats, processCpu, frames);
        }
        catch (Exception ex) { LogFailure("stats refresh", ex); }
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

    private static void ReadTrackVideoMediaType(Mount media)
    {
        var route = media.Route.UseFfmpeg ? "ffmpeg" : media.Transport.IsHls ? "hls" : "native";
        try
        {
            var item = media.Item;
            var index = item?.VideoTracks.SelectedIndex ?? -1;
            media.TrackVideoMediaType = item is not null && index >= 0 && index < item.VideoTracks.Count
                ? DebugVideoMediaType.Describe(route, "track", item.VideoTracks[index].GetEncodingProperties())
                : $"route={route} source=track unavailable (no selected video track)";
        }
        catch (Exception ex) { media.TrackVideoMediaType = $"route={route} source=track unavailable ({ex.GetType().Name})"; }
        Log.Info("player: video media type " + media.TrackVideoMediaType);
    }

    private void OnStatsBufferingStarted(PlaybackBackendSession sender, object? args) => RecordBuffering(sender, starting: true);
    private void OnStatsBufferingEnded(PlaybackBackendSession sender, object? args) => RecordBuffering(sender, starting: false);

    private void RecordBuffering(PlaybackBackendSession sender, bool starting)
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

    /// <summary>
    /// The debug AV1 decoder switch: which decoder this mount's open actually used, from the engine FFmpegInteropX reports
    /// (rav1g's provider reports D3D11), the guard's mark verdict and the fork's "Bale rav1g:" lines.
    /// </summary>
    private static string Av1DecoderRow(Attempt? attempt, Mount? media)
    {
        if (attempt is null || media?.FfmpegSource is null) return "unavailable";
        if (!media.SoftwareVideo || media.Route.VideoCodec != "av1") return $"not software AV1 · switch {attempt.Av1Decoder}";
        var (_, _, fallback, failed, output) = media.Rav1gRoute ? DebugRav1gLog.Snapshot() : default;
        return Rav1gPolicy.Describe(attempt.Av1Decoder, media.Rav1gRoute, Rav1gLibrary.Present,
            media.Rav1gCheck?.GpuEngine ?? (media.Rav1gRoute ? media.Rav1gActive : null), media.Rav1gCheck?.Verdict,
            fallback, failed, output, attempt.Rav1gRemounted);
    }

    /// <summary>IMFMediaEngine's own frame statistics (its counters restart after a seek or pause).</summary>
    private static string EngineFrames(EngineSessionState state) => string.Create(CultureInfo.InvariantCulture,
        $"rendered {state.FramesRendered} · dropped {state.FramesDropped} · corrupted {state.FramesCorrupted} · {(double.IsFinite(state.FramesPerSecond) ? state.FramesPerSecond.ToString("0.00", CultureInfo.InvariantCulture) : "?")} fps · frame delay {(double.IsFinite(state.FrameDelay) ? state.FrameDelay.ToString("0.###", CultureInfo.InvariantCulture) : "?")}");

    private void SetStats(PlaybackStats stats, string? processCpu = null, PlaybackFrameDeliverySnapshot? frames = null)
    {
        var rows = stats.HudRows();
        var media = _attempt?.Media;
        var smoothness = stats.Smoothness;
        string? stageLog = null;
        var stageGpu = media?.StageGpuTiming.ReadRow(out stageLog) ?? "unavailable";
        if (stageLog is not null) Log.Info(stageLog);
        var decoder = media?.FfmpegSource is not null ? DebugDecoderLog.Snapshot() : default;
        rows = [.. rows, ("Player", media?.Player?.Name ?? "unavailable"),
            ("Engine frames", media?.Player is EngineBackend engine ? EngineFrames(engine.LastState) : "unavailable"),
            ("Stalls", smoothness?.StallsText ?? "unavailable"),
            ("Dropped (engine)", media?.Player is EngineBackend && smoothness is { } measured
                ? measured.DroppedFrames.ToString(CultureInfo.InvariantCulture) : "unavailable"),
            ("Decode rate", decoder.DecodeRateText(stats.FrameRate)),
            ("Stage GPU", stageGpu),
            ("Decoder discarded", decoder.DiscardedText),
            ("Starved", decoder.StarvedText),
            ("Playback time", smoothness?.PlaybackTimeText ?? "unavailable"),
            ("HDR output", DebugHdrOutput),
            ("Display mode", DisplayHdrOutput.CurrentModeText()),
            ("Late frames", frames is null ? "unavailable" : string.Create(CultureInfo.InvariantCulture,
                $"{frames.LateFrames} · {frames.RecentLateFrames}/10s ({frames.RecentLateFramesPerSecond:0.0}/s)")),
            ("Stream waits", media?.Stream is { SupportsRanges: true } stream ? string.Create(CultureInfo.InvariantCulture,
                $"{stream.DemandWaits} · max {stream.MaxDemandWaitTime.TotalMilliseconds:0.0} ms · total {stream.DemandWaitTime.TotalMilliseconds:0.0} ms · blocks {stream.BlockFetches} new {stream.BlockAllocations} reused {stream.BlockReuses}") : "unavailable"),
            ("Read-ahead", media?.FfmpegReadAhead ?? "unavailable"),
            ("Stream reads", media?.FfmpegStreamReads ?? "unavailable"),
            ("FFmpeg HDR", media?.FfmpegHdrSupport ?? "unavailable"),
            ("FFmpeg decoder override", media?.FfmpegDecoderOverride ?? "unavailable"),
            ("FFmpeg tone map override", media?.FfmpegToneMapOverride ?? "unavailable"),
            ("FFmpeg colour info override", media?.FfmpegColorInfoOverride ?? "unavailable"),
            ("AV1 decoder", Av1DecoderRow(_attempt, media)),
            ("Video media type (track)", media?.TrackVideoMediaType ?? "unavailable"),
            ("Video media type (mss)", media?.MssVideoMediaType?.Description ?? "unavailable"),
            ("Sample memory", media?.FrameDelivery?.SampleMemory ?? DebugSampleMemory.Unavailable),
            ("Xbox memory", DebugXboxMemory.Describe()),
            ("Delivered ahead", frames?.DeliveredAheadSeconds is { } ahead
                ? ahead.ToString("0.000", CultureInfo.InvariantCulture) + " s" : "unavailable")];
        // Debug-only process load is a Bale diagnostic, after the official HUD rows.
        if (processCpu is not null) rows = [.. rows, ("CPU", processCpu)];
        if (_statsRows.SequenceEqual(rows)) return;
        _statsRows = rows;
        if (DebugStatsVisible) RenderStatsRows();
    }

    /// <summary>Right stick (keyboard U/N) scrolls the stats pane while it's open; true when handled.</summary>
    internal bool ScrollDebugStats(VirtualKey key)
    {
        if (!DebugStatsVisible) return false;
        double? step = key switch
        {
            VirtualKey.GamepadRightThumbstickUp or VirtualKey.U => -60,
            VirtualKey.GamepadRightThumbstickDown or VirtualKey.N => 60,
            _ => null,
        };
        if (step is not { } delta) return false;
        var target = Math.Clamp(StatsScroller.VerticalOffset + delta, 0, StatsScroller.ScrollableHeight);
        StatsScroller.ChangeView(null, target, null);
        return true;
    }

    private void RenderStatsRows()
    {
        StatsLeftRows.Children.Clear();
        StatsRightRows.Children.Clear();
        // Filter only the rendered copy; DebugStatsRows/where retain the complete snapshot.
        var rows = _statsRows.Where(row => !DebugServer.StatsRowFilter.IsHidden(row.Label)).ToArray();
        // TvPlayerHud.kt HudStatsPane: fill top-to-bottom, then across, with the extra row on the left.
        var split = (rows.Length + 1) / 2;
        for (var i = 0; i < rows.Length; i++)
        {
            var (label, value) = rows[i];
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
