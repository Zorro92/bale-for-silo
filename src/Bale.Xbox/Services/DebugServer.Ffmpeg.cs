#if DEBUG_SERVER
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using Bale.Core.Playback;
using FFmpegInteropX;
using Windows.ApplicationModel.Core;
using Windows.Foundation;
using Windows.Media.Playback;
using Windows.UI.Composition;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Hosting;

namespace Bale.Xbox.Services;

internal sealed partial class DebugServer
{
    private static async Task<(string Status, string Body)> PlaytestFfmpegAsync(string? url, string? seconds, string? mode,
        string? threads, string? output10bit, string? rate, string? ahead, string? sws, string? delay, string? prio, string? maxres, string? dpb)
    {
        if (!DebugFfmpegPlaytest.TryParse(url, seconds, mode, out var uri, out var duration, out var decoderMode, out var error)
            || !DebugFfmpegPlaytest.TryParseTuning(threads, output10bit, rate, ahead, sws, delay, prio, maxres, dpb, out var tuning, out error))
            return ("400 Bad Request", error + "\n");
        // Share the built-in probe's gate: never run two isolated players at once.
        if (Interlocked.CompareExchange(ref _playtestBusy, 1, 0) != 0)
            return ("409 Conflict", "a playtest is already running\n");

        var builder = new StringBuilder($"Mode: {decoderMode}\nCPU source: GetProcessTimes (100 ns ticks)\n");
        var failures = new ConcurrentQueue<string>();
        var cpuDiagnostics = new List<string>();
        var didOpen = false;
        var start = TimeSpan.Zero;
        var end = TimeSpan.Zero;
        var maximum = TimeSpan.Zero;
        var playbackState = "unavailable";
        var wall = TimeSpan.Zero;
        TimeSpan? cpuStart = null;
        TimeSpan? cpuEnd = null;
        var buffering = 0;
        var releaseGate = true;
        var playbackRate = tuning.Rate;
        PlaybackFrameDeliverySnapshot? frames = null;
        string? outputPixelFormat = null;

        void Failure(string step, Exception ex)
        {
            var detail = DebugFfmpegPlaytest.Failure(step, ex.HResult);
            failures.Enqueue(detail);
            // Exceptions from media can include grants/URLs; never pass them to Log.Error.
            Log.Info("Debug FFmpeg: " + detail);
        }
        void Attempt(string step, Action action)
        {
            try { action(); }
            catch (Exception ex) { Failure(step, ex); }
        }
        TimeSpan? ReadCpu()
        {
            var value = DebugProcessCpu.Read(out var failure);
            if (failure is not null)
            {
                cpuDiagnostics.Add(failure);
                Log.Info("Debug FFmpeg: " + failure);
            }
            return value;
        }

        try
        {
            Log.Info("Debug FFmpeg: before configuration");
            var config = DebugFfmpegSource.Configuration(decoderMode, tuning);
            builder.Append(DebugFfmpegPlaytest.Settings(config.Video.MaxDecoderThreads, config.Video.VideoOutputAllow10bit,
                Environment.ProcessorCount, tuning, decoderMode));
            Log.Info("Debug FFmpeg: before create");
            var creation = FFmpegMediaSource.CreateFromUriAsync(uri.AbsoluteUri, config).AsTask();
            if (await Task.WhenAny(creation, Task.Delay(DebugFfmpegPlaytest.CreateTimeoutMs)) != creation)
            {
                failures.Enqueue($"create timed out after {DebugFfmpegPlaytest.CreateTimeoutMs} ms");
                Log.Info("Debug FFmpeg: create timed out; awaiting late disposal");
                // Native creation may ignore cancellation. Keep the gate until its eventual source is disposed.
                releaseGate = false;
                _ = DisposeLateFfmpegSourceAsync(creation);
            }
            else
            {
                using var source = await creation;
                Log.Info("Debug FFmpeg: created; before player");
                using var player = await OnFfmpegUiAsync(() => new MediaPlayer { IsMuted = true, AutoPlay = false });
                var renderTarget = new FfmpegRenderTarget();
                DebugFrameDelivery? frameDelivery = null;
                var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                void OnOpened(MediaPlayer sender, object args)
                {
                    try
                    {
                        Log.Info("Debug FFmpeg: opened");
                        opened.TrySetResult(true);
                    }
                    catch (Exception ex) { Failure("MediaOpened handler", ex); opened.TrySetResult(false); }
                }
                void OnFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
                {
                    try
                    {
                        var detail = DebugFfmpegPlaytest.Failure("MediaFailed", args.ExtendedErrorCode?.HResult ?? 0);
                        failures.Enqueue(detail);
                        Log.Info("Debug FFmpeg: " + detail);
                    }
                    catch (Exception ex) { Failure("MediaFailed handler", ex); }
                    finally { opened.TrySetResult(false); stopped.TrySetResult(true); }
                }
                void OnEnded(MediaPlayer sender, object args)
                {
                    try { stopped.TrySetResult(true); }
                    catch (Exception ex) { Failure("MediaEnded handler", ex); }
                }
                void OnBuffering(MediaPlayer sender, object args)
                {
                    try { Interlocked.Increment(ref buffering); }
                    catch (Exception ex) { Failure("BufferingStarted handler", ex); }
                }
                void SnapshotPosition()
                {
                    end = player.PlaybackSession.Position;
                    if (end > maximum) maximum = end;
                }

                try
                {
                    player.MediaOpened += OnOpened;
                    player.MediaFailed += OnFailed;
                    player.MediaEnded += OnEnded;
                    player.BufferingStarted += OnBuffering;
                    Log.Info("Debug FFmpeg: before render target");
                    await OnFfmpegUiAsync(() => { renderTarget.Attach(player); return true; });
                    builder.Append("Surface: composition 160x90\n");
                    Log.Info("Debug FFmpeg: render target attached; before item/open");
                    var item = source.CreateMediaPlaybackItem();
                    // Subscribe after FFmpeg's synchronous sample handler, before any player requests.
                    // Compressed/system-video samples cannot measure decoded frame delivery.
                    if (source.CurrentVideoStream?.DecoderEngine == DecoderEngine.FFmpegSoftwareDecoder)
                        Attempt("frame delivery observer", () => frameDelivery = new DebugFrameDelivery(source,
                            player.PlaybackSession, collectAheadStatistics: true));
                    await OnFfmpegUiAsync(() => { player.Source = item; return true; });
                    if (await Task.WhenAny(opened.Task, Task.Delay(DebugFfmpegPlaytest.OpenTimeoutMs)) == opened.Task)
                        didOpen = await opened.Task;
                    else failures.Enqueue($"open timed out after {DebugFfmpegPlaytest.OpenTimeoutMs} ms");

                    if (didOpen && !stopped.Task.IsCompleted)
                    {
                        source.PlaybackSession = player.PlaybackSession;
                        playbackRate = await OnFfmpegUiAsync(() =>
                        {
                            player.PlaybackSession.PlaybackRate = tuning.Rate;
                            return player.PlaybackSession.PlaybackRate;
                        });
                        builder.Append(string.Create(CultureInfo.InvariantCulture, $"Playback rate: {playbackRate:0.###}\n"));
                        start = player.PlaybackSession.Position;
                        maximum = start;
                        cpuStart = ReadCpu();
                        Log.Info("Debug FFmpeg: before play");
                        var clock = Stopwatch.StartNew();
                        try
                        {
                            await OnFfmpegUiAsync(() => { player.Play(); return true; });
                            // Keep a high-water mark: an end-of-stream reset must not erase observed progress.
                            var deadline = Task.Delay(TimeSpan.FromSeconds(duration));
                            while (!deadline.IsCompleted && !stopped.Task.IsCompleted)
                            {
                                await Task.WhenAny(deadline, stopped.Task, Task.Delay(100));
                                SnapshotPosition();
                            }
                        }
                        finally
                        {
                            wall = clock.Elapsed;
                            Attempt("position snapshot", SnapshotPosition);
                            cpuEnd = ReadCpu();
                        }
                    }
                }
                finally
                {
                    Log.Info("Debug FFmpeg: before snapshot/cleanup");
                    // Capture the state before Pause/Source=null changes it during cleanup.
                    Attempt("playback snapshot", () =>
                    {
                        SnapshotPosition();
                        playbackState = player.PlaybackSession.PlaybackState.ToString();
                    });
                    Attempt("frame delivery snapshot", () =>
                    {
                        frames = frameDelivery?.Read();
                        outputPixelFormat = frameDelivery?.OutputPixelFormat;
                    });
                    Attempt("video snapshot", () =>
                    {
                        if (source.CurrentVideoStream is not { } video) { builder.Append("Video: none\n"); return; }
                        // BitsPerSample is container-dependent (24 for 8-bit H.264, 0 for WebM), not component depth.
                        builder.Append(string.Create(CultureInfo.InvariantCulture,
                            $"Video: codec={DebugScreenText.Compact(video.CodecName, 80)} {video.PixelWidth}x{video.PixelHeight}"
                            + $" fps={video.FramesPerSecond:0.###}"
                            + $" DecoderEngine={DebugFfmpegSource.DecoderName(video.DecoderEngine)}\n"));
                    });
                    Attempt("audio snapshot", () =>
                    {
                        if (source.CurrentAudioStream is not { } audio) { builder.Append("Audio: none\n"); return; }
                        builder.Append($"Audio: codec={DebugScreenText.Compact(audio.CodecName, 80)} channels={audio.Channels}"
                            + $" DecoderEngine={DebugFfmpegSource.DecoderName(audio.DecoderEngine)}\n");
                    });
                    Attempt("detach frame delivery observer", () => frameDelivery?.Dispose());
                    Attempt("unsubscribe MediaOpened", () => player.MediaOpened -= OnOpened);
                    Attempt("unsubscribe MediaFailed", () => player.MediaFailed -= OnFailed);
                    Attempt("unsubscribe MediaEnded", () => player.MediaEnded -= OnEnded);
                    Attempt("unsubscribe BufferingStarted", () => player.BufferingStarted -= OnBuffering);
                    try
                    {
                        await OnFfmpegUiAsync(() =>
                        {
                            Attempt("pause", () => player.Pause());
                            Attempt("detach source", () => player.Source = null);
                            renderTarget.Close(Attempt);
                            return true;
                        });
                    }
                    catch (Exception ex) { Failure("render target cleanup", ex); }
                    Log.Info("Debug FFmpeg: before player/source disposal");
                }
            }
        }
        catch (Exception ex) { Failure("probe", ex); }
        finally
        {
            Log.Info("Debug FFmpeg: done");
            if (releaseGate) Interlocked.Exchange(ref _playtestBusy, 0);
        }

        var reasons = failures.ToArray();
        return ("200 OK", $"Opened: {(didOpen ? "yes" : "no")}\n" + builder
            + $"Output pixel format: {outputPixelFormat ?? "unavailable"}\n"
            + DebugFfmpegPlaytest.FrameDelivery(frames)
            + DebugFfmpegPlaytest.Measurements(start, end, wall, buffering, cpuStart, cpuEnd, maximum, playbackState, playbackRate)
            + (cpuDiagnostics.Count == 0 ? string.Empty : "CPU diagnostics:\n  " + string.Join("\n  ", cpuDiagnostics.Distinct()) + "\n")
            + (reasons.Length == 0 ? string.Empty : "Failures:\n  " + string.Join("\n  ", reasons) + "\n")
            + DebugFfmpegPlaytest.Verdict(didOpen, start, maximum, wall, reasons, playbackRate));
    }

    private static async Task<T> OnFfmpegUiAsync<T>(Func<T> action)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        await CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
        {
            try { done.SetResult(action()); }
            // Unlike the general debug UI helper, propagate without formatting media exception text.
            catch (Exception ex) { done.SetException(ex); }
        });
        return await done.Task;
    }

    private sealed class FfmpegRenderTarget
    {
        private UIElement? _host;
        private Visual? _previous;
        private ContainerVisual? _container;
        private SpriteVisual? _visual;
        private CompositionSurfaceBrush? _brush;
        private MediaPlayerSurface? _surface;
        private bool _replaced;

        internal void Attach(MediaPlayer player)
        {
            _host = Window.Current.Content ?? throw new InvalidOperationException();
            var compositor = ElementCompositionPreview.GetElementVisual(_host).Compositor;
            _previous = ElementCompositionPreview.GetElementChildVisual(_host);
            _container = compositor.CreateContainerVisual();
            _container.Size = new Vector2(160, 90);
            // A visible, nonzero target keeps uncompressed MSS video presenting; opacity 0/offscreen can cull it.
            player.SetSurfaceSize(new Size(160, 90));
            _surface = player.GetSurface(compositor);
            _brush = compositor.CreateSurfaceBrush(_surface.CompositionSurface);
            _visual = compositor.CreateSpriteVisual();
            _visual.Size = new Vector2(160, 90);
            _visual.Brush = _brush;
            _container.Children.InsertAtTop(_visual);
            // Borrow only the root's child-visual slot, preserving any existing composition content.
            _replaced = true;
            ElementCompositionPreview.SetElementChildVisual(_host, null);
            if (_previous is not null) _container.Children.InsertAtBottom(_previous);
            ElementCompositionPreview.SetElementChildVisual(_host, _container);
        }

        internal void Close(Action<string, Action> attempt)
        {
            if (_replaced)
            {
                attempt("detach render visuals", () => _container?.Children.RemoveAll());
                attempt("restore child visual", () => ElementCompositionPreview.SetElementChildVisual(_host!, _previous));
            }
            attempt("detach surface brush", () => { if (_brush is not null) _brush.Surface = null; });
            attempt("dispose render visual", () => _visual?.Dispose());
            attempt("dispose surface brush", () => _brush?.Dispose());
            attempt("dispose player surface", () => _surface?.Dispose());
            attempt("dispose render container", () => _container?.Dispose());
        }
    }

    private static async Task DisposeLateFfmpegSourceAsync(Task<FFmpegMediaSource> creation)
    {
        try
        {
            using var source = await creation;
            Log.Info("Debug FFmpeg: late source before disposal");
        }
        catch (Exception ex)
        {
            Log.Info("Debug FFmpeg: " + DebugFfmpegPlaytest.Failure("late create/dispose", ex.HResult));
        }
        finally
        {
            Log.Info("Debug FFmpeg: late source done");
            Interlocked.Exchange(ref _playtestBusy, 0);
        }
    }
}
#endif
