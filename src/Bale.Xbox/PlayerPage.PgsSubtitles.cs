using Bale.Core.Playback;
using Bale.Xbox.Services;

namespace Bale.Xbox;

public sealed partial class PlayerPage
{
    private PlaybackSubtitleSource? _pgsSource;
    private PgsSubtitleRange? _pgsRange, _pgsLoadingRange;
    private CancellationTokenSource? _pgsFetch;
    private ulong _pgsSink;
    private long _pgsRetryAfter;

    private void StartPgsSubtitles(Attempt attempt, Mount media, PlaybackSubtitleSource source)
    {
        var overlay = EnsureSubtitleOverlay();
        if (overlay is null) return;
        _pgsSink = _subtitleSinkToken = _subtitleSinkToken >= MaxSubtitleSinkToken ? 1 : _subtitleSinkToken + 1;
        if (overlay.BindBitmaps(_pgsSink) < 0) return;
        _pgsSource = source;
        // Like Apple AetherSubtitleOverlay.renderClock's movie-timeline sidecars: SUP PTS is already
        // source time. HLS origin/resume/reanchor mapping happens only once, in SourcePosition.
        _subtitleTimingOrigin = 0;
        RefreshPgsSubtitles(SourcePosition(attempt));
        PushSubtitleClock(force: true);
    }

    private void RefreshPgsSubtitles(double sourceSeconds)
    {
        if (_pgsSource is not { } source || _subtitles is not { } overlay
            || _attempt is not { Media: { FailureQueued: false } media } attempt || !IsCurrent(attempt, media)) return;
        if (_pgsLoadingRange is { } loading)
        {
            if (loading.Contains(sourceSeconds)) return;
            // A backward seek can still be covered by the current window but leave an overlapping
            // prefetch's coverage. Retire it before returning below, or it would replace useful cues.
            _pgsFetch?.Cancel();
            _pgsFetch = null;
            _pgsLoadingRange = null;
        }
        if (_pgsRange is { } loaded && !loaded.NeedsRefresh(sourceSeconds, media.Transport.DurationSeconds)) return;
        if (Environment.TickCount64 < _pgsRetryAfter) return;

        // Outside coverage, remove the old picture immediately, including a seek while paused.
        // Prefetch inside coverage keeps the current window on screen until atomic replacement.
        if (_pgsRange?.Contains(sourceSeconds) != true)
        {
            overlay.BindBitmaps(0);
            overlay.BindBitmaps(_pgsSink);
            _pgsRange = null;
        }
        _pgsFetch?.Cancel();
        var fetch = _pgsFetch = CancellationTokenSource.CreateLinkedTokenSource(media.Token);
        fetch.CancelAfter(TimeSpan.FromSeconds(SubtitleFetchSeconds));
        var range = _pgsLoadingRange = PgsSubtitleRange.At(sourceSeconds);
        _ = LoadPgsSubtitlesAsync(attempt, media, source, range, fetch, overlay, _subtitleGeneration, _pgsSink);
    }

    private async Task LoadPgsSubtitlesAsync(Attempt attempt, Mount media, PlaybackSubtitleSource source,
        PgsSubtitleRange range, CancellationTokenSource fetch, SubtitleOverlay overlay, int generation, ulong sink)
    {
        bool Current() => generation == _subtitleGeneration && ReferenceEquals(_pgsFetch, fetch)
            && ReferenceEquals(_subtitles, overlay) && IsCurrent(attempt, media) && !media.FailureQueued;
        var token = fetch.Token;
        var stage = "fetch";
        try
        {
            // Fetch and decode are bounded and off the UI thread. Serializing the native decode
            // also bounds memory if several seeks supersede workers that are already parsing.
            var width = VideoDimension(attempt.Plan?.Source?.Width);
            var height = VideoDimension(attempt.Plan?.Source?.Height);
            using var window = await Task.Run(async () =>
            {
                var data = await media.Transport.GetPgsSubtitlesAsync(source, range, token).ConfigureAwait(false);
                await _subtitleLoads.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    token.ThrowIfCancellationRequested();
                    return PgsSubtitleWindow.Decode(data, range, width, height);
                }
                finally { _subtitleLoads.Release(); }
            }, token);
            if (!Current() || token.IsCancellationRequested) return;
            stage = "bind";
            var hr = window.Bind(sink, SubtitleClockPush.Milliseconds(SourcePosition(attempt)));
            if (hr < 0)
            {
                Log.Info($"player: subtitles failed stage=pgs_bind hr=0x{unchecked((uint)hr):X8}");
                _pgsRetryAfter = Environment.TickCount64 + 10000;
                return;
            }
            _pgsRange = range;
            _pgsRetryAfter = 0;
            UpdateSubtitleLayout();
            PushSubtitleClock(force: true);
            Log.Info("player: subtitles mode=render format=sup windowed=1");
        }
        catch (OperationCanceledException) when (!Current() || media.Token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (Current()) _pgsRetryAfter = Environment.TickCount64 + 10000;
            // HTTP exceptions can include credentials or payload: HRESULT only.
            Log.Info($"player: subtitles failed stage=pgs_{stage} hr=0x{unchecked((uint)ex.HResult):X8}");
        }
        finally
        {
            if (ReferenceEquals(_pgsFetch, fetch))
            {
                _pgsFetch = null;
                _pgsLoadingRange = null;
            }
            fetch.Dispose();
        }
    }

    private static uint VideoDimension(long? size) => size is > 0 and <= 16384 ? (uint)size.Value : 0;

    private void StopPgsSubtitles()
    {
        _pgsSource = null;
        _pgsRange = _pgsLoadingRange = null;
        _pgsFetch?.Cancel();
        _pgsFetch = null; // The worker owns disposal after its continuation settles.
        _pgsRetryAfter = 0;
        _pgsSink = 0;
    }
}
