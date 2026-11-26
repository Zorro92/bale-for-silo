using System.Globalization;
using Bale.Core;
using Bale.Core.Playback;
using Bale.Core.Settings;
using Bale.Xbox.Services;
using Windows.Media.Playback;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Bale.Xbox;

/// <summary>
/// Hosts Bale's own subtitle renderer (Services/SubtitleOverlay.cs, drawn by bale_gpu.dll into SubtitleLayer):
/// loads the subtitle sidecar each playback plan asks the client to draw, or binds the overlay to the
/// image track the FFmpeg source decodes from the container, feeds the overlay the video
/// rectangle and the SOURCE clock, and ties its D3D device to the player's media lifetime. Everything here
/// runs on the UI thread except Task.Run bodies (HTTP fetch, native font copy and parsing).
/// Logs carry fixed words, counts and HRESULTs only: never URLs, subtitle text or font names.
/// </summary>
public sealed partial class PlayerPage
{
    private const int SubtitlesNotReady = unchecked((int)0x8007139F); // ERROR_INVALID_STATE
    private const int SubtitlesUnavailable = unchecked((int)0x80004005); // E_FAIL
    private const int SubtitleFetchSeconds = 30;
    // How long a text track waits for the attempt's appearance read before it draws in the last one known.
    private static readonly TimeSpan SubtitleAppearanceWait = TimeSpan.FromSeconds(3);
    private const uint MaxSubtitleSinkToken = 999999999;

    /// <summary>One fetched track, kept so a remount of the same plan subtitle does not download it again.</summary>
    private sealed record SubtitlePayload(string Key, byte[] Text, IReadOnlyList<SubtitleFont> Fonts);

    private readonly SubtitleClockPush _subtitleClock = new();
    // Created by the first loaded track: playback without subtitles never creates the D3D device.
    private SubtitleOverlay? _subtitles;
    // One native load at a time, so a superseded load can be cleared before its successor's track lands.
    private readonly SemaphoreSlim _subtitleLoads = new(1, 1);
    private SubtitlePayload? _subtitlePayload;
    // Bumped by every clear: a load that started before it must not leave its track on screen.
    private int _subtitleGeneration;
    // SubtitleArtifactV3.timing_origin_seconds of the loaded track: cue time 0 sits at this source time.
    private double _subtitleTimingOrigin;
    // Last bitmap sink token handed out: 1..MaxSubtitleSinkToken (the wrapper's range), never 0 (0 unbinds).
    private uint _subtitleSinkToken;

    private void InitializeSubtitleLayer()
    {
        SubtitleLayer.SizeChanged += OnSubtitleLayerSizeChanged;
        SubtitleLayer.CompositionScaleChanged += OnSubtitleLayerScaleChanged;
    }

    private void OnSubtitleLayerSizeChanged(object sender, SizeChangedEventArgs e) => UpdateSubtitleLayout();

    // Not guaranteed to arrive on the XAML thread, so marshal like the MediaPlayer callbacks.
    private void OnSubtitleLayerScaleChanged(SwapChainPanel sender, object args)
    {
        if (_subtitles is not null) _ = DispatchAsync(UpdateSubtitleLayout);
    }

    private void AttachSubtitleSession(PlaybackBackendSession session)
    {
        session.NaturalVideoSizeChanged += OnSubtitleVideoSizeChanged;
        session.PlaybackRateChanged += OnSubtitleRateChanged;
    }

    private void DetachSubtitleSession(PlaybackBackendSession session)
    {
        session.NaturalVideoSizeChanged -= OnSubtitleVideoSizeChanged;
        session.PlaybackRateChanged -= OnSubtitleRateChanged;
    }

    private void OnSubtitleVideoSizeChanged(PlaybackBackendSession sender, object? args)
    {
        if (_subtitles is null) return;
        _ = DispatchAsync(() =>
        {
            if (ReferenceEquals(_attempt?.Media?.Player?.PlaybackSession, sender)) UpdateSubtitleLayout();
        });
    }

    private void OnSubtitleRateChanged(PlaybackBackendSession sender, object? args)
    {
        if (_subtitles is null) return;
        _ = DispatchAsync(() =>
        {
            if (ReferenceEquals(_attempt?.Media?.Player?.PlaybackSession, sender)) PushSubtitleClock();
        });
    }

    /// <summary>The panel in physical pixels and the Stretch="Uniform" video rectangle inside it.</summary>
    private SubtitleOverlayLayout CurrentSubtitleLayout()
    {
        uint videoWidth = 0, videoHeight = 0;
        try
        {
            // Zero until the pipeline knows its video size; the overlay then uses the whole panel.
            if (_attempt?.Media is { Opened: true, Player: { } player })
            {
                videoWidth = player.PlaybackSession.NaturalVideoWidth;
                videoHeight = player.PlaybackSession.NaturalVideoHeight;
            }
        }
        catch (Exception ex) { LogFailure("subtitle video size", ex); }
        return SubtitleOverlayLayout.Compute(SubtitleLayer.ActualWidth, SubtitleLayer.ActualHeight,
            SubtitleLayer.CompositionScaleX, SubtitleLayer.CompositionScaleY, videoWidth, videoHeight);
    }

    private void UpdateSubtitleLayout()
    {
        if (_subtitles is not { } overlay) return;
        try { overlay.SetLayout(CurrentSubtitleLayout()); }
        catch (Exception ex) { LogFailure("subtitle layout", ex); }
    }

    /// <summary>
    /// Hands the overlay the absolute SOURCE position (the clock skips, seeks and progress reports use),
    /// rate and whether it is running. SubtitleClockPush drops calls that would not change the overlay's
    /// extrapolation, so this is safe to call from every 100 ms tick; seeks force a push.
    /// </summary>
    private void PushSubtitleClock(bool force = false)
    {
        if (_subtitles is not { } overlay) return;
        try
        {
            // A seek in flight reports a clock that is about to jump; SeekAsync pushes when it ends.
            if (_seeking && !force) return;
            Attempt? current = null;
            var playing = false;
            var rate = 1.0;
            if (_attempt is { Media: { Opened: true, FailureQueued: false, Player: { } player } media } attempt
                && IsCurrent(attempt, media))
            {
                current = attempt;
                // "playing" only once the clock really moves (UpdatePlaybackState), not during the first load.
                playing = _state == "playing";
                rate = SubtitleClockPush.Rate(player.PlaybackSession.PlaybackRate);
            }
            var position = current is null ? _sourcePosition : SourcePosition(current);
            RefreshPgsSubtitles(position);
            if (!_subtitleClock.ShouldPush(playing, rate, Environment.TickCount64, force)) return;
            overlay.SetClock(SubtitleClockPush.Milliseconds(position - _subtitleTimingOrigin), rate, playing);
        }
        catch (Exception ex) { LogFailure("subtitle clock", ex); }
    }

    /// <summary>Created by the first track that needs it; null when bale_gpu.dll cannot provide one.</summary>
    private SubtitleOverlay? EnsureSubtitleOverlay()
    {
        if (_subtitles is { } existing) return existing;
        var overlay = _subtitles = SubtitleOverlay.Create(SubtitleLayer, CurrentSubtitleLayout());
        if (overlay is not null) _subtitleClock.Reset();
        return overlay;
    }

    /// <summary>
    /// Called for every new mount, after ClearSubtitles and before the FFmpeg source is created: when
    /// the plan's subtitle decision is a sidecar for the client to draw (PlaybackSubtitleResolver),
    /// fetch and load it without holding up the mount. An in-container image track is bound here
    /// instead, so the mount's FFmpeg source can be told its stream and sink. A seek reanchor or
    /// recovery remount therefore gets its track back. Failures leave playback running without subtitles.
    /// </summary>
    private void StartSubtitles(Attempt attempt, Mount media)
    {
        try
        {
#if DEBUG_SERVER
            if (attempt.DebugUrl is not null) return;
#endif
            // silo-apple ApplePlaybackV3PlanAdapter.swift invalidEmbeddedSubtitle: a native selection the
            // client cannot mount as planned (not original_http, or no usable stream index).
            if (PlaybackEmbeddedSubtitleFailure.PlanFailure(attempt.Plan) is { } invalid)
            {
                _subtitlePayload = null;
                Log.Info("player: subtitles failed stage=embedded plan_" + invalid);
                NoteEmbeddedSubtitleFailure(attempt, media, "plan_" + invalid);
                return;
            }
            // Embedded PGS draws from the server's .sup windows, which cover a cue that began before a
            // seek's keyframe; the demuxer would skip it until the next display set.
            if (attempt.Client.Playback.ResolveEmbeddedPgs(attempt.Plan!) is { } pgs)
            {
                _subtitlePayload = null;
                StartPgsSubtitles(attempt, media, pgs);
                return;
            }
            if (attempt.Client.Playback.ResolveEmbeddedSubtitles(attempt.Plan!) is { } embedded)
            {
                // No sidecar to fetch or reuse.
                _subtitlePayload = null;
                StartEmbeddedSubtitles(attempt, media, embedded);
                return;
            }
            if (attempt.Client.Playback.ResolveSubtitles(attempt.Plan!) is not { } source)
            {
                // Off, burned in or unusable: nothing this plan could reuse.
                _subtitlePayload = null;
                return;
            }
            if (source.IsBitmap)
            {
                _subtitlePayload = null;
                StartPgsSubtitles(attempt, media, source);
                return;
            }
            _ = LoadPlanSubtitlesAsync(attempt, media, source, _subtitleGeneration);
        }
        catch (Exception ex) { LogFailure("subtitle plan", ex); }
    }

    /// <summary>
    /// Binds the overlay to a fresh sink token and records it on the mount; the FFmpeg source created
    /// next pushes that stream's decoded bitmaps to it (FfmpegSource.ApplySubtitleSink).
    /// </summary>
    private void StartEmbeddedSubtitles(Attempt attempt, Mount media, PlaybackEmbeddedSubtitle embedded)
    {
        // Only the FFmpeg demuxer can hand over the stream; the server claim is direct MKV, which uses it.
        if (!media.Route.UseFfmpeg)
        {
            Log.Info("player: subtitles failed stage=embedded route");
            NoteEmbeddedSubtitleFailure(attempt, media, "route");
            return;
        }
        var overlay = EnsureSubtitleOverlay();
        if (overlay is null)
        {
            Log.Info($"player: subtitles failed stage=embedded hr=0x{unchecked((uint)SubtitlesUnavailable):X8}");
            NoteEmbeddedSubtitleFailure(attempt, media, "overlay");
            return;
        }
        var token = _subtitleSinkToken = _subtitleSinkToken >= MaxSubtitleSinkToken ? 1 : _subtitleSinkToken + 1;
        var hr = overlay.BindBitmaps(token);
        if (hr < 0)
        {
            Log.Info($"player: subtitles failed stage=embedded hr=0x{unchecked((uint)hr):X8}");
            NoteEmbeddedSubtitleFailure(attempt, media, "bind");
            return;
        }
        media.SubtitleStreamIndex = embedded.StreamIndex;
        media.SubtitleSinkToken = token;
        // Bitmap times are plain source time.
        _subtitleTimingOrigin = 0;
        UpdateSubtitleLayout();
        PushSubtitleClock(force: true);
        Log.Info($"player: subtitles mode=render format=bitmap stream={embedded.StreamIndex.ToString(CultureInfo.InvariantCulture)}");
    }

    /// <summary>
    /// Records that this mount cannot draw its plan's native embedded subtitle (PlaybackEmbeddedSubtitleFailure;
    /// a sidecar, packaged artifact or burn-in plan is never native and is ignored). A mount still opening sends
    /// the recovery from OnMediaOpened, before Play; an opened one (a subtitle answer adopted in place) sends it now.
    /// </summary>
    private void NoteEmbeddedSubtitleFailure(Attempt attempt, Mount media, string stage)
    {
        if (!PlaybackEmbeddedSubtitleFailure.IsNative(attempt.Plan)) return;
        media.EmbeddedSubtitleFailure ??= stage;
        if (media.Opened) RecoverEmbeddedSubtitle(attempt, media);
    }

    /// <summary>
    /// silo-apple protocolV3LoadFailureRecovery: a failed native embedded selection uses the single failure replan
    /// with subtitle_embedded_failed. Once that budget is spent the video plays on without subtitles. True when queued.
    /// </summary>
    private bool RecoverEmbeddedSubtitle(Attempt attempt, Mount media)
    {
        if (media.EmbeddedSubtitleFailure is not { } stage) return false;
        if (!PlaybackEmbeddedSubtitleFailure.Replans(attempt.Plan, attempt.Replanned))
        {
            Log.Info($"player: embedded subtitle unavailable stage={stage}; recovery spent, playing without subtitles");
            return false;
        }
        Log.Info($"player: embedded subtitle unavailable stage={stage}; replanning");
        QueueFailure(attempt, media, 0, isDecoder: false, isNetwork: false, PlaybackEmbeddedSubtitleFailure.Failure);
        return true;
    }

    private async Task LoadPlanSubtitlesAsync(Attempt attempt, Mount media, PlaybackSubtitleSource source, int generation)
    {
        var stage = "fetch";
        var holdsLoad = false;
        // Captured now: DisposeMount cancels, then disposes, the source this token came from.
        var token = media.Token;
        bool Current() => generation == _subtitleGeneration && IsCurrent(attempt, media) && !media.FailureQueued;
        try
        {
            var payload = _subtitlePayload;
            if (payload?.Key != source.CacheKey)
            {
                _subtitlePayload = null;
                var transport = media.Transport;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(SubtitleFetchSeconds));
                var fetchToken = deadline.Token;
                payload = await Task.Run(async () =>
                {
                    var text = await transport.GetSubtitleAsync(source, fetchToken).ConfigureAwait(false);
                    IReadOnlyList<SubtitleFont> fonts = [];
                    try { fonts = await transport.GetSubtitleFontsAsync(source, fetchToken).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // The script still draws: libass substitutes system fonts.
                        Log.Info($"player: subtitles failed stage=fonts hr=0x{unchecked((uint)ex.HResult):X8}");
                    }
                    return new SubtitlePayload(source.CacheKey, text, fonts);
                }, fetchToken);
                if (!Current()) return;
                _subtitlePayload = payload;
            }

            stage = "load";
            await Task.WhenAny(attempt.SubtitleAppearanceRead, Task.Delay(SubtitleAppearanceWait, token));
            await _subtitleLoads.WaitAsync(token);
            holdsLoad = true;
            if (!Current()) return;
            var overlay = EnsureSubtitleOverlay();
            if (overlay is null)
            {
                Log.Info($"player: subtitles failed stage=load hr=0x{unchecked((uint)SubtitlesUnavailable):X8}");
                return;
            }
            var loading = payload;
            var style = SubtitleStyle(attempt.Client);
            // Fonts first: add_font applies to the next load. Copying up to 32 MiB of fonts and parsing
            // the script block, so neither runs on the UI thread; SubtitleOverlay.Dispose waits for them.
            var hr = await Task.Run(() =>
            {
                // A font that fails to register is not fatal: libass falls back to system fonts.
                foreach (var font in loading.Fonts) overlay.AddFont(font.Name, font.Data);
                return overlay.LoadText(source.Format, loading.Text, style);
            });
            if (!ReferenceEquals(_subtitles, overlay)) return; // Disposed meanwhile; nothing is on screen.
            if (!Current())
            {
                // The mount changed during the parse, after its own clear: this track belongs to the old
                // mount. The successor's load is still waiting for _subtitleLoads, so it lands after this.
                overlay.Clear();
                return;
            }
            if (hr < 0)
            {
                Log.Info($"player: subtitles failed stage=load hr=0x{unchecked((uint)hr):X8}");
                return;
            }
            _subtitleTimingOrigin = source.TimingOriginSeconds;
            UpdateSubtitleLayout();
            PushSubtitleClock(force: true);
            Log.Info($"player: subtitles mode={source.Mode} format={source.FormatName} fonts={loading.Fonts.Count} bytes={loading.Text.Length}");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // HRESULT only: an HTTP or JSON exception message can carry the URL or payload.
            Log.Info($"player: subtitles failed stage={stage} hr=0x{unchecked((uint)ex.HResult):X8}");
        }
        finally
        {
            if (holdsLoad) _subtitleLoads.Release();
        }
    }

    /// <summary>
    /// Debug loads only: replaces the subtitle track of the current mount with UTF-8 text in an overlay
    /// format (0 ASS/SSA, 1 SubRip, 2 WebVTT), creating the overlay on first use. Fonts are added before
    /// the load so the script can use them. Returns an HRESULT.
    /// </summary>
    internal int LoadSubtitles(uint format, byte[] utf8, IReadOnlyList<(string Name, byte[] Data)>? fonts = null)
    {
        try
        {
            if (_attempt is not { Media: { FailureQueued: false } media } attempt || !IsCurrent(attempt, media))
                return SubtitlesNotReady;
            var overlay = EnsureSubtitleOverlay();
            if (overlay is null) return SubtitlesUnavailable;
            if (fonts is not null)
            {
                foreach (var (name, data) in fonts)
                {
                    // A font that fails to register is not fatal: libass falls back to system fonts.
                    overlay.AddFont(name, data);
                }
            }
            var hr = overlay.LoadText(format, utf8, SubtitleStyle(attempt.Client));
            if (hr < 0) return hr;
            // A hand-loaded file's cue times are plain source time.
            _subtitleTimingOrigin = 0;
            UpdateSubtitleLayout();
            PushSubtitleClock(force: true);
            return hr;
        }
        catch (Exception ex)
        {
            LogFailure("subtitle load", ex);
            return ex.HResult < 0 ? ex.HResult : SubtitlesUnavailable;
        }
    }

    /// <summary>
    /// What SubRip and WebVTT cues are drawn in: the subtitle appearance (Settings › Subtitles), with the console's
    /// caption settings over it while Use Device Settings is on. ASS keeps its own styles and image subtitles their
    /// pixels (Android TV's SubtitleManager.kt applyAppearance; docs/parity-backlog.md lists what is left).
    /// </summary>
    private static SubtitleTextStyle SubtitleStyle(SiloClient client)
    {
        var settings = client.SubtitleSettings;
        return SubtitleTextStyle.For(settings.EffectiveAppearance(settings.MatchDevice ? SystemCaptions.Read() : null));
    }

    /// <summary>The appearance as the server resolves it when a playback starts; a failed read keeps the last one known.</summary>
    private static async Task ReadSubtitleAppearanceAsync(SiloClient client)
    {
        try { await client.SubtitleSettings.RefreshAsync(); }
        catch (Exception ex) { LogFailure("subtitle appearance read", ex); }
    }

    /// <summary>
    /// A new mount starts without the previous mount's track; the overlay itself is kept. Also retires
    /// any load still in flight (its generation is now stale).
    /// </summary>
    private void ClearSubtitles()
    {
        _subtitleGeneration++;
        StopPgsSubtitles();
        _subtitleTimingOrigin = 0;
        if (_subtitles is not { } overlay) return;
        // Clear does not unbind: without this the old mount's image track would keep drawing.
        Cleanup("unbind subtitles", () => overlay.BindBitmaps(0));
        Cleanup("clear subtitles", () => overlay.Clear());
    }

    /// <summary>Releases the overlay's render thread, D3D device and swap chain with the player's media.</summary>
    /// <remarks>
    /// Recovery keeps the fetched track for the mount that follows; stopping the attempt releases it.
    /// An in-flight worker load is waited for inside Dispose, then finds its generation stale.
    /// </remarks>
    private void DisposeSubtitles(bool keepFetched = false)
    {
        _subtitleGeneration++;
        StopPgsSubtitles();
        _subtitleTimingOrigin = 0;
        if (!keepFetched) _subtitlePayload = null;
        if (_subtitles is not { } overlay) return;
        _subtitles = null;
        _subtitleClock.Reset();
        Cleanup("unbind subtitles", () => overlay.BindBitmaps(0));
        Cleanup("dispose subtitles", overlay.Dispose);
    }

#if DEBUG_SERVER
    internal string DebugSubtitleStats
    {
        get
        {
            if (_subtitles is not { } overlay) return "State: not created\n";
            if (!overlay.TryGetStats(out var stats)) return "State: stats unavailable\n";
            return string.Create(CultureInfo.InvariantCulture,
                $"State: {(stats.Events == 0 ? "empty" : "loaded")}\nEvents: {stats.Events}\nPresents: {stats.Presents}\n"
                + $"Renders: {stats.Renders}\nLast render ms: {stats.LastRenderUs / 1000.0:0.###}\n"
                + $"Max render ms: {stats.MaxRenderUs / 1000.0:0.###}\nImages: {stats.Images}\nSize: {stats.Width}x{stats.Height}\n");
        }
    }

    internal (string Status, string Body) DebugLoadSubtitles(uint format, byte[] utf8, IReadOnlyList<(string Name, byte[] Data)> fonts)
    {
        var hr = LoadSubtitles(format, utf8, fonts);
        if (hr == SubtitlesNotReady) return ("409 Conflict", "no active player pipeline\n");
        if (hr < 0) return ("500 Internal Server Error", $"subtitle overlay load failed hr=0x{unchecked((uint)hr):X8}\n");
        return ("200 OK", $"Loaded: fonts={fonts.Count}\n" + DebugSubtitleStats);
    }

    internal (string Status, string Body) DebugClearSubtitles()
    {
        ClearSubtitles();
        return ("200 OK", "Cleared\n" + DebugSubtitleStats);
    }
#endif
}
