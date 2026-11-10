using System.Globalization;
using Bale.Core.Playback;
using Bale.Xbox.Services;
using Windows.Media.Playback;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Bale.Xbox;

/// <summary>
/// Hosts Bale's own subtitle renderer (Services/SubtitleOverlay.cs, drawn by bale_gpu.dll into SubtitleLayer):
/// feeds it the video rectangle and the SOURCE clock, and ties its D3D device to the player's media lifetime.
/// Everything here runs on the UI thread.
/// </summary>
public sealed partial class PlayerPage
{
    private const int SubtitlesNotReady = unchecked((int)0x8007139F); // ERROR_INVALID_STATE
    private const int SubtitlesUnavailable = unchecked((int)0x80004005); // E_FAIL

    private readonly SubtitleClockPush _subtitleClock = new();
    // Created by the first loaded track: playback without subtitles never creates the D3D device.
    private SubtitleOverlay? _subtitles;

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

    private void AttachSubtitleSession(MediaPlaybackSession session)
    {
        session.NaturalVideoSizeChanged += OnSubtitleVideoSizeChanged;
        session.PlaybackRateChanged += OnSubtitleRateChanged;
    }

    private void DetachSubtitleSession(MediaPlaybackSession session)
    {
        session.NaturalVideoSizeChanged -= OnSubtitleVideoSizeChanged;
        session.PlaybackRateChanged -= OnSubtitleRateChanged;
    }

    private void OnSubtitleVideoSizeChanged(MediaPlaybackSession sender, object args)
    {
        if (_subtitles is null) return;
        _ = DispatchAsync(() =>
        {
            if (ReferenceEquals(_attempt?.Media?.Player?.PlaybackSession, sender)) UpdateSubtitleLayout();
        });
    }

    private void OnSubtitleRateChanged(MediaPlaybackSession sender, object args)
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
            if (!_subtitleClock.ShouldPush(playing, rate, Environment.TickCount64, force)) return;
            var position = current is null ? _sourcePosition : SourcePosition(current);
            overlay.SetClock(SubtitleClockPush.Milliseconds(position), rate, playing);
        }
        catch (Exception ex) { LogFailure("subtitle clock", ex); }
    }

    /// <summary>
    /// Replaces the subtitle track of the current mount with ASS/SSA script text, creating the overlay on
    /// first use. Fonts are added before the load so the script can use them. Returns an HRESULT.
    /// </summary>
    internal int LoadSubtitles(string ass, IReadOnlyList<(string Name, byte[] Data)>? fonts = null)
    {
        try
        {
            if (_attempt is not { Media: { FailureQueued: false } media } attempt || !IsCurrent(attempt, media))
                return SubtitlesNotReady;
            var overlay = _subtitles;
            if (overlay is null)
            {
                overlay = _subtitles = SubtitleOverlay.Create(SubtitleLayer, CurrentSubtitleLayout());
                if (overlay is null) return SubtitlesUnavailable;
                _subtitleClock.Reset();
            }
            if (fonts is not null)
            {
                foreach (var (name, data) in fonts)
                {
                    // A font that fails to register is not fatal: libass falls back to system fonts.
                    overlay.AddFont(name, data);
                }
            }
            var hr = overlay.LoadAss(ass);
            if (hr < 0) return hr;
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

    /// <summary>A new mount starts without the previous mount's track; the overlay itself is kept.</summary>
    private void ClearSubtitles()
    {
        if (_subtitles is { } overlay) Cleanup("clear subtitles", () => overlay.Clear());
    }

    /// <summary>Releases the overlay's render thread, D3D device and swap chain with the player's media.</summary>
    private void DisposeSubtitles()
    {
        if (_subtitles is not { } overlay) return;
        _subtitles = null;
        _subtitleClock.Reset();
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

    internal (string Status, string Body) DebugLoadSubtitles(string ass, IReadOnlyList<(string Name, byte[] Data)> fonts)
    {
        var hr = LoadSubtitles(ass, fonts);
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
