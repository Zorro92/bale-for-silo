#if DEBUG_SERVER
using System.Runtime.InteropServices;
using Bale.Core.Playback;
using Bale.Xbox.Services;
using Windows.Storage.Streams;
using System.Globalization;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;

namespace Bale.Xbox;

/// <summary>
/// Debug: `present-engine` hands this mount's routed source to IMFMediaEngine (DebugEnginePresent)
/// instead of MediaPlayer, records the report, then leaves. It runs after routing, so it plays what
/// Bale would: the FFmpegInteropX source (tone map on SDR, Dolby Vision profile 8 base layer, software
/// decode) through the engine extension, or the authenticated stream through Windows' own reader for
/// direct play. HDMI was already set up by the mount like any playback.
/// </summary>
public sealed partial class PlayerPage
{
    private static string RouteText(Mount media)
    {
        var route = media.Route;
        if (media.Adaptive is not null) return "Windows HLS (AdaptiveMediaSource)";
        return route.UseFfmpeg
            ? "ffmpeg " + (route.ConsoleToneMap ? "d3d11va + console tone map" : route.DolbyVision7BaseLayer ? "DV7 base layer"
                : route.SoftwareVideo ? "software decode" : route.DolbyVisionBaseLayer ? "system decoder, DV8 base layer" : "system decoder passthrough")
            : "Windows reader (direct)";
    }

    private async Task PresentThroughEngineAsync(Mount media, string mime, int seconds, Bale.Core.Api.Models.PlaybackPlan plan)
    {
        var present = DebugPresentEngineSwitch.Mode != "frames";
        var frameRate = PlaybackStats.FromPlan(plan).FrameRate is { } fps && double.IsFinite(fps) && fps > 0 ? fps : 0;
        var route = media.Route;
        if (DebugPresentEngineSwitch.Mode == "player")
        {
            await StartEnginePlayerAsync(media, mime);
            return;
        }
        var routeText = RouteText(media);
        var report = $"Library playback through the engine ({(present ? "rendering mode" : "frame-server, decoded frames counted")}; route {routeText}; {mime}; "
            + $"plan frame rate {(frameRate > 0 ? frameRate.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "unknown")})\n";
        // The output guard would replan this attempt from the engine's frames; the probe owns the mount now.
        media.VideoOutputGuard?.Dispose();
        media.VideoOutputGuard = null;
        var host = new DebugEnginePresent();
        var owned = new List<nint>();
        try
        {
            Log.Info("player: present-engine start");
            if (present) report += await host.OpenAsync(hdr: false);
            if (media.FfmpegSource is { } ffmpeg)
            {
                // The MSS exists once CreateMediaPlaybackItem installed its handlers (the mount did that).
                var mss = ffmpeg.GetMediaStreamSource();
                var pointer = ((WinRT.IWinRTObject)mss).NativeObject.ThisPtr;
                var body = await RunEngineAsync(() => host.RunSource(pointer, seconds, frameRate, present));
                if (body.Contains("MF_MEDIASOURCE_SERVICE): fails", StringComparison.Ordinal) || body.Contains("at QI IMFGetService", StringComparison.Ordinal))
                {
                    // MediaPlayer consumes the WinRT MediaSource wrapper; its IMFGetService can expose the IMFMediaSource.
                    using var wrapper = Windows.Media.Core.MediaSource.CreateFromMediaStreamSource(mss);
                    var wrapperPointer = ((WinRT.IWinRTObject)wrapper).NativeObject.ThisPtr;
                    body = "Attempt 1 (MediaStreamSource) failed; attempt 2 (MediaSource wrapper)\n"
                        + await RunEngineAsync(() => host.RunSource(wrapperPointer, seconds, frameRate, present));
                    GC.KeepAlive(wrapper);
                }
                GC.KeepAlive(mss);
                report += body;
            }
            else if (media.RandomStream is { } random)
            {
                var pointer = WinRT.MarshalInterface<IRandomAccessStream>.FromManaged(random);
                owned.Add(pointer);
                report += await RunEngineAsync(() => host.RunStream(pointer, mime, seconds, frameRate, present));
            }
            else if (media.Adaptive is { } adaptiveSource)
            {
                // Windows' HLS source (Bale supplies every download through DownloadRequested). The WinRT
                // MediaSource wrapper exposes no IMFGetService, so hand over the adaptive source itself.
                var pointer = ((WinRT.IWinRTObject)adaptiveSource).NativeObject.ThisPtr;
                report += await RunEngineAsync(() => host.RunSource(pointer, seconds, frameRate, present));
                GC.KeepAlive(adaptiveSource);
            }
            else report += "Route not covered by the probe\n";
        }
        catch (Exception ex) { report += $"Present error: {ex.GetType().Name} 0x{unchecked((uint)ex.HResult):X8}\n"; }
        finally
        {
            if (present) report += await host.CloseAsync();
            foreach (var pointer in owned) Marshal.Release(pointer);
        }
        DebugPresentEngineSwitch.Report(report);
        // Step names, HRESULTs and counts only: the report never holds the URL.
        foreach (var line in report.Split('\n', StringSplitOptions.RemoveEmptyEntries)) Log.Info("present-engine: " + line);
        // Leave once this mount (and the start lifecycle holding it) has returned.
        _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () => OnBack(closeControls: false));
    }

    // ---- Interactive engine player (present-engine player) ----

    private DebugEnginePresent? _enginePlayer;
    private DispatcherTimer? _engineTimer;
    private string _engineRoute = string.Empty;
    private bool _engineStatsVisible = true;
    private (long Ticks, ulong Rendered, ulong Dropped) _engineSample;
    private (ulong Rendered, ulong Dropped) _engineLastSecond;
    // The engine restarts its counters on seek and pause; keep running totals across those.
    private (ulong Rendered, ulong Dropped, ulong Corrupted) _engineBase, _engineRaw;
    private bool _engineClosing;

    internal bool EnginePlayerActive => _enginePlayer is { HasSession: true };

    /// <summary>Plays this mount's routed source in an engine session until Back, end or error.</summary>
    private async Task StartEnginePlayerAsync(Mount media, string mime)
    {
        media.VideoOutputGuard?.Dispose();
        media.VideoOutputGuard = null;
        _engineRoute = RouteText(media);
        // Fork note (2026-11-17): FFmpegInteropX FastSeek needs FFmpegMediaSource.PlaybackSession to be a
        // MediaPlayer session; the engine player has none, so every seek is a full seek. For software
        // AV1 that is the 4K seek that ran zero-copy out of GPU memory (0x8007000E): unverified here.
        if (media.FfmpegSource is not null && media.Route.SoftwareVideo
            && string.Equals(media.Route.VideoCodec, "av1", StringComparison.OrdinalIgnoreCase))
            _engineRoute += " · AV1 software: full seeks only, 4K seeks unverified";
        var host = new DebugEnginePresent();
        var report = $"Engine player (route {_engineRoute}; {mime}; start {media.StartPosition:0.###}s)\n";
        try
        {
            report += await host.OpenAsync(hdr: false);
            var start = media.StartPosition;
            var paused = media.StartPaused;
            if (media.FfmpegSource is { } ffmpeg)
            {
                var mss = ffmpeg.GetMediaStreamSource();
                var pointer = ((WinRT.IWinRTObject)mss).NativeObject.ThisPtr;
                report += await RunEngineAsync(() => host.OpenSession(pointer, null, start, paused));
                if (!host.HasSession && report.Contains("MF_MEDIASOURCE_SERVICE): fails", StringComparison.Ordinal))
                {
                    var wrapper = Windows.Media.Core.MediaSource.CreateFromMediaStreamSource(mss);
                    var wrapperPointer = ((WinRT.IWinRTObject)wrapper).NativeObject.ThisPtr;
                    report += "Attempt 2 (MediaSource wrapper)\n" + await RunEngineAsync(() => host.OpenSession(wrapperPointer, null, start, paused));
                    media.EngineKeepAlive = wrapper;
                }
            }
            else if (media.RandomStream is { } random)
            {
                var pointer = WinRT.MarshalInterface<IRandomAccessStream>.FromManaged(random);
                try { report += await RunEngineAsync(() => host.OpenSession(pointer, mime, start, paused)); }
                finally { Marshal.Release(pointer); }
            }
            else if (media.Adaptive is { } adaptiveSource && media.Source is { } wrapped)
            {
                // The WinRT MediaSource wrapper exposes no IMFGetService; try the adaptive source itself first.
                var pointer = ((WinRT.IWinRTObject)adaptiveSource).NativeObject.ThisPtr;
                report += "Attempt 1 (AdaptiveMediaSource)\n" + await RunEngineAsync(() => host.OpenSession(pointer, null, start, paused));
                if (!host.HasSession)
                {
                    var wrappedPointer = ((WinRT.IWinRTObject)wrapped).NativeObject.ThisPtr;
                    report += "Attempt 2 (MediaSource wrapper)\n" + await RunEngineAsync(() => host.OpenSession(wrappedPointer, null, start, paused));
                }
            }
            else report += "Route not covered by the engine player\n";
        }
        catch (Exception ex) { report += $"Engine player error: {ex.GetType().Name} 0x{unchecked((uint)ex.HResult):X8}\n"; }
        foreach (var line in report.Split('\n', StringSplitOptions.RemoveEmptyEntries)) Log.Info("engine-player: " + line);
        DebugPresentEngineSwitch.Report(report);
        if (!host.HasSession)
        {
            DebugPresentEngineSwitch.Report(report + await host.CloseAsync());
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () => OnBack(closeControls: false));
            return;
        }
        _enginePlayer = host;
        _engineClosing = false;
        _engineSample = (Environment.TickCount64, 0, 0);
        _engineBase = _engineRaw = default;
        SetLoading(false);
        _engineTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _engineTimer.Tick += (_, _) => UpdateEnginePlayer();
        _engineTimer.Start();
        UpdateEnginePlayer();
    }

    private void UpdateEnginePlayer()
    {
        try
        {
            if (_enginePlayer is not { } host || !host.TryState(out var state)) return;
            if (state.FramesRendered < _engineRaw.Rendered || state.FramesDropped < _engineRaw.Dropped)
                _engineBase = (_engineBase.Rendered + _engineRaw.Rendered, _engineBase.Dropped + _engineRaw.Dropped,
                    _engineBase.Corrupted + _engineRaw.Corrupted);
            _engineRaw = (state.FramesRendered, state.FramesDropped, state.FramesCorrupted);
            state.FramesRendered += _engineBase.Rendered;
            state.FramesDropped += _engineBase.Dropped;
            state.FramesCorrupted += _engineBase.Corrupted;
            var now = Environment.TickCount64;
            if (now - _engineSample.Ticks >= 1000)
            {
                _engineLastSecond = (state.FramesRendered - Math.Min(state.FramesRendered, _engineSample.Rendered),
                    state.FramesDropped - Math.Min(state.FramesDropped, _engineSample.Dropped));
                _engineSample = (now, state.FramesRendered, state.FramesDropped);
            }
            if (state.Ended || state.Failed)
            {
                Log.Info($"engine-player: {(state.Failed ? "engine error" : "ended")} at {state.Position:0.###}s");
                _ = CloseEnginePlayerAsync(leave: true);
                return;
            }
            var status = state.Seeking ? "seeking" : state.Waiting ? "buffering" : state.Paused ? "paused" : "playing";
            var hours = state.Duration >= 3600;
            var clock = PlaybackTransportFeedback.Clock(state.Position, forceHours: hours);
            var total = double.IsFinite(state.Duration) ? PlaybackTransportFeedback.Clock(state.Duration, forceHours: hours) : "?";
            host.SetOverlay(!_engineStatsVisible ? null : FormattableString.Invariant(
                $"Engine player · {_engineRoute}\n{clock} / {total} · {status}\nVideo {state.Width}x{state.Height} · {state.FramesPerSecond:0.###} fps\nFrames shown {state.FramesRendered} · dropped {state.FramesDropped} · corrupted {state.FramesCorrupted}\nLast second +{_engineLastSecond.Rendered} shown / +{_engineLastSecond.Dropped} dropped\nJitter {state.Jitter:0.###} · frame delay {state.FrameDelay:0.###}\nA pause · LT/RT -10/+30 s · LB/RB chapters · View stats · B leave"));
        }
        catch (Exception ex) { LogFailure("engine player tick", ex); }
    }

    /// <summary>Keys while the engine player is up (Bale's map); true when handled.</summary>
    private bool HandleEngineKey(VirtualKey key, bool keyDown, bool firstDown)
    {
        if (_enginePlayer is not { HasSession: true } host) return false;
        if (key is VirtualKey.GamepadView or VirtualKey.I)
        {
            if (keyDown && firstDown)
            {
                _engineStatsVisible = !_engineStatsVisible;
                UpdateEnginePlayer();
            }
            return true;
        }
        if (key is VirtualKey.GamepadB or VirtualKey.Escape or VirtualKey.GoBack)
        {
            if (keyDown && firstDown) _ = CloseEnginePlayerAsync(leave: true);
            return true;
        }
        var action = PlaybackRemoteKeys.Action((int)key, keyDown, firstDown, cleanPlayback: true);
        if (action == PlaybackKeyAction.None) return false;
        if (action == PlaybackKeyAction.ConsumeOnly || !host.TryState(out var state)) return true;
        switch (action)
        {
            case PlaybackKeyAction.PlayPause or PlaybackKeyAction.Select:
                Log.Info($"engine-player: {(state.Paused ? "play" : "pause")} hr=0x{unchecked((uint)(state.Paused ? host.Play() : host.Pause())):X8}");
                break;
            case PlaybackKeyAction.SkipBack or PlaybackKeyAction.SkipForward:
                var target = PlaybackSeek.SkipTarget(state.Position, state.Duration, action == PlaybackKeyAction.SkipForward);
                Log.Info($"engine-player: seek {target:0.###}s hr=0x{unchecked((uint)host.Seek(target)):X8}");
                break;
            case PlaybackKeyAction.PreviousChapter or PlaybackKeyAction.NextChapter:
                if (PlaybackChapters.Target(PlayingChapters().Select(chapter => chapter.StartSeconds), state.Position,
                        action == PlaybackKeyAction.NextChapter) is { } chapter)
                    Log.Info($"engine-player: chapter {chapter:0.###}s hr=0x{unchecked((uint)host.Seek(chapter)):X8}");
                break;
            case PlaybackKeyAction.Stop:
                _ = CloseEnginePlayerAsync(leave: true);
                break;
            case PlaybackKeyAction.OpenOptions:
                _engineStatsVisible = !_engineStatsVisible;
                break;
        }
        UpdateEnginePlayer();
        return true;
    }

    /// <summary>Debug server play/pause/toggle for the engine player.</summary>
    internal Task<(string Status, string Body)> EnginePlaybackAsync(DebugPlaybackControl request)
    {
        if (_enginePlayer is not { HasSession: true } host || !host.TryState(out var state))
            return Task.FromResult(("409 Conflict", "no engine player\n"));
        var pause = request == DebugPlaybackControl.Pause || request == DebugPlaybackControl.Toggle && !state.Paused;
        var hr = pause ? host.Pause() : host.Play();
        UpdateEnginePlayer();
        return Task.FromResult(hr >= 0
            ? ("200 OK", string.Create(CultureInfo.InvariantCulture, $"Engine player: {(pause ? "paused" : "playing")} at {state.Position:0.###}s\n"))
            : ("500 Internal Server Error", $"engine hr=0x{unchecked((uint)hr):X8}\n"));
    }

    /// <summary>Debug server seek for the engine player (engine time).</summary>
    internal Task<(string Status, string Body)> EngineSeekAsync(double seconds, bool pause)
    {
        if (_enginePlayer is not { HasSession: true } host) return Task.FromResult(("409 Conflict", "no engine player\n"));
        var hr = host.Seek(seconds);
        if (hr >= 0 && pause) hr = host.Pause();
        UpdateEnginePlayer();
        return Task.FromResult(hr >= 0
            ? ("200 OK", string.Create(CultureInfo.InvariantCulture, $"Engine player: seek {seconds:0.###}s{(pause ? " paused" : string.Empty)}\n"))
            : ("500 Internal Server Error", $"engine hr=0x{unchecked((uint)hr):X8}\n"));
    }

    /// <summary>Stops the engine player (UI thread); <paramref name="leave"/> also leaves the player.</summary>
    private async Task CloseEnginePlayerAsync(bool leave)
    {
        if (_engineClosing || _enginePlayer is not { } host) return;
        _engineClosing = true;
        _engineTimer?.Stop();
        _engineTimer = null;
        string closing;
        try { closing = await host.CloseAsync(); }
        catch (Exception ex) { closing = $"close error {ex.GetType().Name}\n"; }
        _enginePlayer = null;
        foreach (var line in closing.Split('\n', StringSplitOptions.RemoveEmptyEntries)) Log.Info("engine-player: " + line);
        DebugPresentEngineSwitch.Report(DebugPresentEngineSwitch.LastReport + closing);
        if (leave) OnBack(closeControls: false);
    }

    private static Task<string> RunEngineAsync(Func<string> run) =>
        Task.Factory.StartNew(run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
}
#endif
