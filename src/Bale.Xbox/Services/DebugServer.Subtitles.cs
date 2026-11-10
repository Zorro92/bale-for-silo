#if DEBUG_SERVER
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using FFmpegInteropX;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace Bale.Xbox.Services;

/// <summary>
/// Subtitle probe: plays a clip muted on Windows' own source (native) or FFmpegInteropX (ffmpeg),
/// optionally with a sidecar file, and reports every timed-metadata track and the cues it raises.
/// Tracks run ApplicationPresented, so cues are counted, not drawn. Production playback is unchanged.
/// </summary>
internal sealed partial class DebugServer
{
    private const int SubtitleProbeMaxSeconds = 40;

    private static async Task<(string Status, string Body)> SubtitleProbeAsync(string? url, string? route, string? seconds,
        string? sidecar, string? index)
    {
        if (!DebugPlaytest.TryParse(url, seconds ?? "31", out var uri, out var duration, out var error, SubtitleProbeMaxSeconds))
            return ("400 Bad Request", error + "\n");
        route ??= "native";
        if (route is not ("native" or "ffmpeg")) return ("400 Bad Request", "route must be native or ffmpeg\n");
        Uri? sidecarUri = null;
        Uri? indexUri = null;
        if (sidecar is not null && !DebugPlaytest.TryParse(sidecar, null, out sidecarUri, out _, out error))
            return ("400 Bad Request", "sidecar: " + error + "\n");
        if (index is not null && (sidecarUri is null || route != "native"))
            return ("400 Bad Request", "index needs a sidecar on the native route (VobSub .sub + .idx)\n");
        if (index is not null && !DebugPlaytest.TryParse(index, null, out indexUri, out _, out error))
            return ("400 Bad Request", "index: " + error + "\n");
        if (Interlocked.CompareExchange(ref _playtestBusy, 1, 0) != 0)
            return ("409 Conflict", "a playtest is already running\n");

        var report = new StringBuilder($"Route: {route}\n");
        var failures = new ConcurrentQueue<string>();
        var notes = new ConcurrentQueue<string>();
        var tracks = new List<SubtitleProbeTrack>();
        var didOpen = false;
        var position = TimeSpan.Zero;
        var avCounts = "";
        FFmpegMediaSource? ffmpeg = null;
        MediaSource? native = null;
        var releaseGate = true;

        void Failure(string step, Exception ex)
        {
            var detail = DebugFfmpegPlaytest.Failure(step, ex.HResult);
            failures.Enqueue(detail);
            // Media exceptions can carry URLs; log only the step and HRESULT.
            Log.Info("Debug subtitles: " + detail);
        }
        void Attempt(string step, Action action)
        {
            try { action(); }
            catch (Exception ex) { Failure(step, ex); }
        }

        try
        {
            MediaPlaybackItem item;
            if (route == "ffmpeg")
            {
                var config = new MediaSourceConfig();
                config.Video.VideoDecoderMode = VideoDecoderMode.ForceSystemDecoder;
                config.Audio.SystemDecoderAAC = true;
                config.Subtitles.AutoSelectForcedSubtitles = false;
                var creation = FFmpegMediaSource.CreateFromUriAsync(uri.AbsoluteUri, config).AsTask();
                if (await Task.WhenAny(creation, Task.Delay(DebugFfmpegPlaytest.CreateTimeoutMs)) != creation)
                {
                    releaseGate = false;
                    _ = DisposeLateFfmpegSourceAsync(creation);
                    return ("200 OK", report + $"Opened: no\nFailures:\n  create timed out after {DebugFfmpegPlaytest.CreateTimeoutMs} ms\n");
                }
                ffmpeg = await creation;
                if (sidecarUri is not null)
                {
                    try
                    {
                        using var stream = await RandomAccessStreamReference.CreateFromUri(sidecarUri).OpenReadAsync();
                        var added = await ffmpeg.AddExternalSubtitleAsync(stream, "sidecar");
                        report.Append($"Sidecar: added {added.Count} stream(s)\n");
                    }
                    catch (Exception ex) { Failure("sidecar add", ex); }
                }
                item = ffmpeg.CreateMediaPlaybackItem();
            }
            else
            {
                native = MediaSource.CreateFromUri(uri);
                if (sidecarUri is not null)
                {
                    var timedText = indexUri is null
                        ? TimedTextSource.CreateFromUri(sidecarUri)
                        : TimedTextSource.CreateFromUriWithIndex(sidecarUri, indexUri);
                    timedText.Resolved += (_, args) =>
                    {
                        try
                        {
                            // Raised off the request thread: queue it, append after playback.
                            notes.Enqueue(args.Error is { } resolveError
                                ? $"Sidecar: resolve failed code={resolveError.ErrorCode} hr=0x{resolveError.ExtendedError?.HResult ?? 0:X8}\n"
                                : $"Sidecar: resolved {args.Tracks.Count} track(s)\n");
                        }
                        catch (Exception ex) { Failure("sidecar resolved", ex); }
                    };
                    native.ExternalTimedTextSources.Add(timedText);
                }
                item = new MediaPlaybackItem(native);
            }

            var player = await OnFfmpegUiAsync(() => new MediaPlayer { IsMuted = true, AutoPlay = false });
            var renderTarget = new FfmpegRenderTarget();
            var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnOpened(MediaPlayer sender, object args) => opened.TrySetResult(true);
            void OnFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
            {
                failures.Enqueue(DebugFfmpegPlaytest.Failure("MediaFailed", args.ExtendedErrorCode?.HResult ?? 0));
                opened.TrySetResult(false);
            }
            // Attach every new track: count its cues and keep it out of the platform renderer.
            void AttachNew()
            {
                var list = item.TimedMetadataTracks;
                for (var i = tracks.Count; i < list.Count; i++)
                {
                    var track = list[i];
                    var probe = new SubtitleProbeTrack(i, DescribeTrack(track));
                    tracks.Add(probe);
                    track.CueEntered += probe.OnCue;
                    track.TrackFailed += probe.OnFailed;
                    list.SetPresentationMode((uint)i, TimedMetadataTrackPresentationMode.ApplicationPresented);
                }
            }

            try
            {
                player.MediaOpened += OnOpened;
                player.MediaFailed += OnFailed;
                await OnFfmpegUiAsync(() => { renderTarget.Attach(player); return true; });
                await OnFfmpegUiAsync(() => { player.Source = item; return true; });
                if (await Task.WhenAny(opened.Task, Task.Delay(DebugFfmpegPlaytest.OpenTimeoutMs)) == opened.Task)
                    didOpen = await opened.Task;
                else failures.Enqueue($"open timed out after {DebugFfmpegPlaytest.OpenTimeoutMs} ms");
                if (didOpen)
                {
                    if (ffmpeg is not null) ffmpeg.PlaybackSession = player.PlaybackSession;
                    await OnFfmpegUiAsync(() => { AttachNew(); player.Play(); return true; });
                    var deadline = Task.Delay(TimeSpan.FromSeconds(duration));
                    while (!deadline.IsCompleted)
                    {
                        await Task.WhenAny(deadline, Task.Delay(250));
                        // Native sources can add tracks after MediaOpened.
                        await OnFfmpegUiAsync(() => { AttachNew(); return true; });
                    }
                    position = player.PlaybackSession.Position;
                    avCounts = await OnFfmpegUiAsync(() => $"Video/audio tracks: {item.VideoTracks.Count}/{item.AudioTracks.Count}\n");
                }
            }
            finally
            {
                Attempt("unsubscribe MediaOpened", () => player.MediaOpened -= OnOpened);
                Attempt("unsubscribe MediaFailed", () => player.MediaFailed -= OnFailed);
                await OnFfmpegUiAsync(() =>
                {
                    Attempt("pause", () => player.Pause());
                    Attempt("detach source", () => player.Source = null);
                    renderTarget.Close(Attempt);
                    Attempt("dispose player", player.Dispose);
                    return true;
                });
            }

            if (ffmpeg is not null)
            {
                Attempt("ffmpeg streams", () =>
                {
                    foreach (var stream in ffmpeg.SubtitleStreams)
                        report.Append($"FFmpeg stream: codec={DebugScreenText.Compact(stream.CodecName ?? "", 40)}"
                            + $" name={DebugScreenText.Compact(stream.Name ?? "", 40)} lang={stream.Language}"
                            + $" default={stream.IsDefault} forced={stream.IsForced} external={stream.IsExternal}\n");
                });
            }
        }
        catch (Exception ex) { Failure("probe", ex); }
        finally
        {
            Attempt("dispose ffmpeg source", () => ffmpeg?.Dispose());
            Attempt("dispose native source", () => native?.Dispose());
            if (releaseGate) Interlocked.Exchange(ref _playtestBusy, 0);
        }

        report.Insert(0, $"Opened: {(didOpen ? "yes" : "no")}\n");
        foreach (var note in notes) report.Append(note);
        report.Append(string.Create(CultureInfo.InvariantCulture, $"Position: {position.TotalSeconds:0.0}s of {duration}s\n"));
        report.Append(avCounts);
        report.Append($"Tracks: {tracks.Count}\n");
        foreach (var track in tracks) report.Append(track.Report());
        var reasons = failures.ToArray();
        if (reasons.Length > 0) report.Append("Failures:\n  " + string.Join("\n  ", reasons) + "\n");
        return ("200 OK", report.ToString());
    }

    private static string DescribeTrack(TimedMetadataTrack track) =>
        $"kind={track.TimedMetadataKind} label={DebugScreenText.Compact(track.Label ?? "", 40)}"
        + $" lang={DebugScreenText.Compact(track.Language ?? "", 16)} id={DebugScreenText.Compact(track.Id ?? "", 24)}";

    private sealed class SubtitleProbeTrack(int index, string description)
    {
        private const int MaxCues = 10;
        private readonly ConcurrentQueue<string> _cues = new();
        private int _count;
        private int _text;
        private int _image;
        private string? _failure;

        internal void OnCue(TimedMetadataTrack sender, MediaCueEventArgs args)
        {
            try
            {
                var cue = args.Cue;
                // Cues arrive as plain IInspectable wrappers, so a type test never matches; query the interface.
                var text = TryAs<TimedTextCue>(cue);
                var image = text is null ? TryAs<ImageCue>(cue) : null;
                if (text is not null) Interlocked.Increment(ref _text);
                else if (image is not null) Interlocked.Increment(ref _image);
                if (Interlocked.Increment(ref _count) <= MaxCues) _cues.Enqueue(DescribeCue(cue, text, image));
            }
            catch (Exception ex) { _cues.Enqueue($"cue read failed hr=0x{ex.HResult:X8}"); }
        }

        internal void OnFailed(TimedMetadataTrack sender, TimedMetadataTrackFailedEventArgs args)
        {
            try { _failure = $"code={args.Error.ErrorCode} hr=0x{args.Error.ExtendedError?.HResult ?? 0:X8}"; }
            catch (Exception ex) { _failure = $"failure read failed hr=0x{ex.HResult:X8}"; }
        }

        internal string Report()
        {
            var builder = new StringBuilder($"Track {index}: {description} cues={_count} text={_text} image={_image}");
            if (_failure is not null) builder.Append(" failed ").Append(_failure);
            builder.Append('\n');
            foreach (var cue in _cues) builder.Append("  ").Append(cue).Append('\n');
            return builder.ToString();
        }

        private static T? TryAs<T>(object value) where T : class
        {
            if (value is T direct) return direct;
            try { return WinRT.CastExtensions.As<T>(value); }
            catch (InvalidCastException) { return null; }
        }

        private static string DescribeCue(IMediaCue cue, TimedTextCue? text, ImageCue? image)
        {
            var time = string.Create(CultureInfo.InvariantCulture, $"{cue.StartTime.TotalSeconds:0.00}s+{cue.Duration.TotalSeconds:0.00}");
            if (text is not null)
            {
                {
                    var lines = text.Lines.Select(line => line.Text ?? "").ToList();
                    var builder = new StringBuilder($"{time} text lines={lines.Count} \"{DebugScreenText.Compact(string.Join(" / ", lines), 90)}\"");
                    if (text.CueStyle is { } style)
                        builder.Append($" style={style.FontStyle}/{style.FontWeight}/{Colour(style.Foreground)}");
                    if (text.CueRegion is { } region)
                        builder.Append(string.Create(CultureInfo.InvariantCulture,
                            $" region=({region.Position.X:0.#},{region.Position.Y:0.#}{region.Position.Unit}) align={region.DisplayAlignment}/{text.CueStyle?.LineAlignment}"));
                    var subformats = text.Lines.SelectMany(line => line.Subformats)
                        .Select(sub => sub.SubformatStyle is { } s
                            ? $"{sub.StartIndex}+{sub.Length}:{s.FontStyle}/{s.FontWeight}/{Colour(s.Foreground)}"
                            : $"{sub.StartIndex}+{sub.Length}")
                        .ToList();
                    if (subformats.Count > 0) builder.Append(" sub=[").Append(string.Join(" ", subformats)).Append(']');
                    return builder.ToString();
                }
            }
            if (image is not null)
                return string.Create(CultureInfo.InvariantCulture,
                    $"{time} image pos=({image.Position.X:0.#},{image.Position.Y:0.#}{image.Position.Unit})"
                    + $" extent={image.Extent.Width:0.#}x{image.Extent.Height:0.#}{image.Extent.Unit}"
                    + $" bitmap={image.SoftwareBitmap?.PixelWidth ?? 0}x{image.SoftwareBitmap?.PixelHeight ?? 0}");
            if (TryAs<DataCue>(cue) is { } data) return $"{time} data bytes={data.Data?.Length ?? 0}";
            return $"{time} {cue.GetType().Name}";
        }

        private static string Colour(Windows.UI.Color colour) => $"#{colour.A:X2}{colour.R:X2}{colour.G:X2}{colour.B:X2}";
    }
}
#endif
