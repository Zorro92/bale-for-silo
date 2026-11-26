#if DEBUG_SERVER
using System.Globalization;
#endif
using Bale.Core.Playback;
using Bale.Xbox.Services;
using FFmpegInteropX;

namespace Bale.Xbox;

/// <summary>
/// In-band closed captions (CEA-608 in the video's A/53 cc_data; docs/closed-captions.md). Each FFmpeg mount of an H.264,
/// HEVC or MPEG-2 stream gets its own bale_gpu caption decoder (<see cref="ClosedCaptions"/>) under a fresh bale_cc_sink
/// token, disposed with the mount (DisposeMount). The subtitle lists offer CC once a decoder has seen CC1 captions; picking
/// it draws the mount's captions on Bale's subtitle overlay in the subtitle appearance, keeping their own 608 colours and
/// italics. The decisions (what is offered, what a pick does, what a remount keeps) are Bale.Core's
/// <see cref="PlaybackClosedCaptions"/>, one per attempt. UI thread throughout; logs carry fixed words, never caption text.
/// </summary>
public sealed partial class PlayerPage
{
    private const long CaptionsCheckMs = 1000;
    // Process-wide, so no token is handed out again while an older FFmpeg source may still be closing on the pool.
    private static long s_captionToken;
    private long _captionsCheckedAt;

    /// <summary>1..ClosedCaptions.MaxToken, the fork's range for bale_cc_sink (0 takes no packets).</summary>
    private static ulong NextCaptionToken() =>
        (ulong)(Interlocked.Increment(ref s_captionToken) % (long)ClosedCaptions.MaxToken) + 1;

    /// <summary>
    /// Every mount, as its source is configured (<paramref name="config"/> null for Windows' own source): a plan that plays a
    /// server subtitle ends CC, and an FFmpeg source of a tapped codec gets this mount's own decoder and token (a remount
    /// gets new ones). While CC is on, the new decoder is drawn from its first packet, so a recovery replan, a seek reanchor,
    /// an HLS remount or a quality change keeps the viewer's captions. Failures leave playback running without captions.
    /// </summary>
    private void StartCaptions(Attempt attempt, Mount media, MediaSourceConfig? config)
    {
        try
        {
            var draw = attempt.Captions.PlanPlays(attempt.Plan);
            if (config is null || !PlaybackClosedCaptions.Taps(media.Route))
            {
                if (draw) Log.Info("player: closed captions unavailable on this route");
                return;
            }
            // Null when bale_gpu.dll or its caption ABI is missing: the mount plays without captions.
            if (ClosedCaptions.Create() is not { } decoder) return;
            var token = NextCaptionToken();
            if (decoder.Register(token) < 0)
            {
                decoder.Dispose();
                return;
            }
            media.CaptionDecoder = decoder;
            media.CaptionToken = token;
            FfmpegSource.ApplyCaptionSink(config, token);
            if (draw) _ = AttachCaptionsAsync(attempt, media);
        }
        catch (Exception ex) { LogFailure("closed captions", ex); }
    }

    /// <summary>
    /// Draws <paramref name="media"/>'s captions on the overlay from now on, starting with the ones its decoder kept (the tap
    /// runs ahead of the clock, so a late attach loses nothing). One overlay track change at a time (_subtitleLoads): a
    /// sidecar of an earlier plan still parsing finishes, and clears what it replaced, before the captions take the overlay;
    /// a clear or mount after this call (a newer generation) owns the overlay instead. Their times are the mount's
    /// MediaStreamSample timeline, which is the player clock: the source clock less the mount's timeline offset (0 on direct
    /// play, the keyframe origin of a reanchored progressive remux, the first segment of an HLS mount after
    /// StartPlayerClockAt), so that offset is the overlay's timing origin. Completes at once when no load runs.
    /// </summary>
    private async Task AttachCaptionsAsync(Attempt attempt, Mount media)
    {
        if (media.CaptionDecoder is not { } decoder)
        {
            Log.Info("player: closed captions unavailable on this mount");
            return;
        }
        var generation = _subtitleGeneration;
        var token = media.Token;
        var holdsLoad = false;
        try
        {
            await _subtitleLoads.WaitAsync(token);
            holdsLoad = true;
            if (generation != _subtitleGeneration || !attempt.Captions.Selected || !ReferenceEquals(attempt.Media, media)
                || !ReferenceEquals(media.CaptionDecoder, decoder)) return;
            var overlay = EnsureSubtitleOverlay();
            if (overlay is null)
            {
                Log.Info($"player: subtitles failed stage=captions hr=0x{unchecked((uint)SubtitlesUnavailable):X8}");
                return;
            }
            // Owner, 2026-11-25 (docs/closed-captions.md): captions keep their own 608 colours, italics and underline over
            // the appearance, so the player never asks for Android TV's plain look.
            var hr = overlay.AttachCaptions(decoder, SubtitleStyle(attempt.Client));
            if (hr < 0)
            {
                Log.Info($"player: subtitles failed stage=captions hr=0x{unchecked((uint)hr):X8}");
                return;
            }
            media.CaptionsAttached = true;
            _subtitleTimingOrigin = media.Transport.Timeline.TimelineOffsetSeconds;
            UpdateSubtitleLayout();
            PushSubtitleClock(force: true);
            Log.Info("player: subtitles mode=render format=cea608 channel=cc1");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { LogFailure("closed captions", ex); }
        finally
        {
            if (holdsLoad) _subtitleLoads.Release();
        }
    }

    /// <summary>Stops drawing <paramref name="media"/>'s captions; the overlay drops their track if it is still theirs.</summary>
    private static void DetachCaptions(Mount? media)
    {
        if (media is not { CaptionDecoder: { } decoder, CaptionsAttached: true }) return;
        media.CaptionsAttached = false;
        Cleanup("detach closed captions", () => decoder.Detach());
        Log.Info("player: closed captions off");
    }

    /// <summary>
    /// A subtitle answer adopted in place (TryAdoptSubtitleInPlace, after its ClearSubtitles): a server subtitle it plays ends
    /// CC; otherwise CC stays on the playing mount, drawn again on the cleared overlay.
    /// </summary>
    private void AdoptCaptions(Attempt attempt, Mount media)
    {
        try
        {
            if (attempt.Captions.PlanPlays(attempt.Plan)) _ = AttachCaptionsAsync(attempt, media);
            else DetachCaptions(media);
        }
        catch (Exception ex) { LogFailure("closed captions", ex); }
    }

    /// <summary>
    /// A subtitle row of the control bar's menu or the Options panel. CC is the player's own (PickClosedCaptionsAsync); Off
    /// ends CC at once and a server subtitle once a plan plays it, and both then take the ordinary track change.
    /// </summary>
    private Task PickSubtitleAsync(int index)
    {
        if (index == PlaybackClosedCaptions.TrackIndex) return PickClosedCaptionsAsync();
        if (_attempt is { } attempt && attempt.Captions.PickTrack(index))
        {
            DetachCaptions(attempt.Media);
            UpdateTrackValues();
        }
        return ChangeTracksAsync(null, index);
    }

    /// <summary>
    /// The CC row: CC goes on without a track_change replan, and nothing is remembered. A server subtitle on screen goes first,
    /// through the Off path (remember: false, so that Off is no subtitle preference either), so the server stops its burn-in
    /// or sidecar; the plan the Off brings then draws CC (StartCaptions on a remount, AdoptCaptions in place).
    /// </summary>
    private async Task PickClosedCaptionsAsync()
    {
        if (_attempt is not { Media: { Opened: true, FailureQueued: false } media } attempt || !IsCurrent(attempt, media)
            || !attempt.Captions.Offered) return;
        if (attempt.Captions.Selected)
        {
            Log.Info("player: closed captions already on");
            return;
        }
        if (attempt.Captions.Pick(attempt.Plan))
        {
            Log.Info("player: closed captions picked; turning the server subtitle off first");
            await ChangeTracksAsync(null, -1, remember: false);
            // Refused, busy or failed: the server's subtitle stays, and CC is off again.
            if (!attempt.Captions.PlanPlays(attempt.Plan)) Log.Info("player: closed captions not on: the server subtitle stayed");
        }
        else
        {
            Log.Info("player: closed captions picked");
            await AttachCaptionsAsync(attempt, media);
        }
        UpdateTrackValues();
    }

    /// <summary>
    /// The 100 ms tick: about once a second, until this attempt has found captions, asks the mount's decoder what it has seen.
    /// The first CC1 captions add the CC row, which the menus and the Options panel list the next time they open.
    /// </summary>
    private void UpdateCaptions()
    {
        if (_attempt is not { Media: { CaptionDecoder: { } decoder } media } attempt || attempt.Captions.Discovered
            || !IsCurrent(attempt, media)) return;
        var now = Environment.TickCount64;
        if (now - _captionsCheckedAt < CaptionsCheckMs) return;
        _captionsCheckedAt = now;
        try
        {
            if (!attempt.Captions.Observe(decoder.Seen())) return;
            Log.Info("player: closed captions found channel=cc1");
            UpdateTrackValues();
        }
        catch (Exception ex) { LogFailure("closed captions", ex); }
    }

#if DEBUG_SERVER
    /// <summary>The where line's Captions: what the attempt offers and picked, and the mount's decoder and its counters.</summary>
    internal string DebugCaptions
    {
        get
        {
            var captions = _attempt?.Captions;
            var text = $"offered={YesNo(captions?.Offered)} picked={YesNo(captions?.Selected)}";
            if (_attempt?.Media is not { } media) return text + " decoder=none (no mount)";
            if (media.CaptionDecoder is not { } decoder)
                return text + $" decoder=none (route={(media.Route.UseFfmpeg ? "ffmpeg" : "windows")} codec={media.Route.VideoCodec ?? "unknown"})";
            if (!decoder.TryGetStats(out var stats)) return text + " decoder=unavailable";
            return text + string.Create(CultureInfo.InvariantCulture,
                $" attached={YesNo(media.CaptionsAttached)} token={media.CaptionToken} seen={SeenText(stats.Seen)} packets={stats.Packets}"
                + $" captioned={stats.CaptionPackets} pairs={stats.Pairs} cues={stats.Cues} late={stats.Late} depth={stats.Depth}"
                + $" flushes={stats.Flushes} origin={_subtitleTimingOrigin:0.###}s");

            static string YesNo(bool? value) => value == true ? "yes" : "no";
        }
    }

    /// <summary>bale_cc_seen bits in words: the channels that carried captions, any cc_data, CEA-708 data.</summary>
    private static string SeenText(uint seen)
    {
        var parts = new List<string>();
        if ((seen & ClosedCaptions.SeenCc1) != 0) parts.Add("cc1");
        if ((seen & ClosedCaptions.SeenCc2) != 0) parts.Add("cc2");
        if ((seen & ClosedCaptions.SeenCc3) != 0) parts.Add("cc3");
        if ((seen & ClosedCaptions.SeenCc4) != 0) parts.Add("cc4");
        if ((seen & ClosedCaptions.SeenCcData) != 0) parts.Add("data");
        if ((seen & ClosedCaptions.SeenDtvcc) != 0) parts.Add("708");
        return parts.Count == 0 ? "none" : string.Join(',', parts);
    }

    /// <summary>
    /// bale-debug.sh captions cc|off: the subtitle menu's CC or Off row through the menu's own path (Off also turns a server
    /// subtitle off, and on a Silo session saves Off as the viewer's pick, as the row does).
    /// </summary>
    internal async Task<(string Status, string Body)> DebugPickCaptionsAsync(string action)
    {
        if (_attempt is not { Media: { Opened: true, FailureQueued: false } media } attempt || !IsCurrent(attempt, media))
            return ("409 Conflict", "no active player pipeline\n");
        if (action == "cc" && !attempt.Captions.Offered)
            return ("409 Conflict", "no closed captions found on this playback yet\nCaptions: " + DebugCaptions + "\n");
        await PickSubtitleAsync(action == "cc" ? PlaybackClosedCaptions.TrackIndex : -1);
        return ("200 OK", "Captions: " + DebugCaptions + "\n");
    }
#endif
}
