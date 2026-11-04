#if DEBUG_SERVER
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using FFmpegInteropX;
using Windows.Media.Playback;
using Windows.System.Diagnostics;

namespace Bale.Xbox.Services;

internal sealed partial class DebugServer
{
    private static async Task<(string Status, string Body)> PlaytestFfmpegAsync(string? url, string? seconds, string? mode)
    {
        if (!DebugFfmpegPlaytest.TryParse(url, seconds, mode, out var uri, out var duration, out var decoderMode, out var error))
            return ("400 Bad Request", error + "\n");
        // Share the built-in probe's gate: never run two isolated players at once.
        if (Interlocked.CompareExchange(ref _playtestBusy, 1, 0) != 0)
            return ("409 Conflict", "a playtest is already running\n");

        var builder = new StringBuilder($"Mode: {decoderMode}\n");
        var failures = new ConcurrentQueue<string>();
        var didOpen = false;
        var start = TimeSpan.Zero;
        var end = TimeSpan.Zero;
        var wall = TimeSpan.Zero;
        TimeSpan? cpuStart = null;
        TimeSpan? cpuEnd = null;
        var buffering = 0;
        var releaseGate = true;

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
            try
            {
                var report = ProcessDiagnosticInfo.GetForCurrentProcess().CpuUsage.GetReport();
                return report is null ? null : report.UserTime + report.KernelTime;
            }
            catch (Exception ex)
            {
                Log.Info("Debug FFmpeg: " + DebugFfmpegPlaytest.Failure("CPU unavailable", ex.HResult));
                return null;
            }
        }

        try
        {
            Log.Info("Debug FFmpeg: before configuration");
            // The native default logger can print complete URLs. A null provider drops all FFmpeg logs.
            FFmpegInteropLogging.SetLogProvider(null);
            var config = new MediaSourceConfig();
            config.Video.VideoDecoderMode = decoderMode switch
            {
                "software" => VideoDecoderMode.ForceFFmpegSoftwareDecoder,
                "system" => VideoDecoderMode.ForceSystemDecoder,
                _ => VideoDecoderMode.Automatic,
            };
            // Bound individual HTTP reads too; the overall creation deadline below covers probing.
            config.FFmpegOptions["rw_timeout"] = "10000000";
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
                using var player = new MediaPlayer { IsMuted = true, AutoPlay = false };
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

                try
                {
                    player.MediaOpened += OnOpened;
                    player.MediaFailed += OnFailed;
                    player.MediaEnded += OnEnded;
                    player.BufferingStarted += OnBuffering;
                    Log.Info("Debug FFmpeg: before item/open");
                    var item = source.CreateMediaPlaybackItem();
                    player.Source = item;
                    if (await Task.WhenAny(opened.Task, Task.Delay(DebugFfmpegPlaytest.OpenTimeoutMs)) == opened.Task)
                        didOpen = await opened.Task;
                    else failures.Enqueue($"open timed out after {DebugFfmpegPlaytest.OpenTimeoutMs} ms");

                    if (didOpen && !stopped.Task.IsCompleted)
                    {
                        source.PlaybackSession = player.PlaybackSession;
                        start = player.PlaybackSession.Position;
                        cpuStart = ReadCpu();
                        Log.Info("Debug FFmpeg: before play");
                        var clock = Stopwatch.StartNew();
                        try
                        {
                            player.Play();
                            // End short sample clips promptly so the ratio measures only their playback interval.
                            await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(duration)), stopped.Task);
                        }
                        finally
                        {
                            wall = clock.Elapsed;
                            Attempt("position snapshot", () => end = player.PlaybackSession.Position);
                            cpuEnd = ReadCpu();
                        }
                    }
                }
                finally
                {
                    Log.Info("Debug FFmpeg: before snapshot/cleanup");
                    Attempt("pause", () => player.Pause());
                    Attempt("video snapshot", () =>
                    {
                        if (source.CurrentVideoStream is not { } video) { builder.Append("Video: none\n"); return; }
                        builder.Append(string.Create(CultureInfo.InvariantCulture,
                            $"Video: codec={DebugScreenText.Compact(video.CodecName, 80)} {video.PixelWidth}x{video.PixelHeight}"
                            + $" bits={video.BitsPerSample} fps={video.FramesPerSecond:0.###}"
                            + $" DecoderEngine={DescribeFfmpegDecoder(video.DecoderEngine)}\n"));
                    });
                    Attempt("audio snapshot", () =>
                    {
                        if (source.CurrentAudioStream is not { } audio) { builder.Append("Audio: none\n"); return; }
                        builder.Append($"Audio: codec={DebugScreenText.Compact(audio.CodecName, 80)} channels={audio.Channels}"
                            + $" DecoderEngine={DescribeFfmpegDecoder(audio.DecoderEngine)}\n");
                    });
                    Attempt("unsubscribe MediaOpened", () => player.MediaOpened -= OnOpened);
                    Attempt("unsubscribe MediaFailed", () => player.MediaFailed -= OnFailed);
                    Attempt("unsubscribe MediaEnded", () => player.MediaEnded -= OnEnded);
                    Attempt("unsubscribe BufferingStarted", () => player.BufferingStarted -= OnBuffering);
                    Attempt("detach source", () => player.Source = null);
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
            + DebugFfmpegPlaytest.Measurements(start, end, wall, buffering, cpuStart, cpuEnd)
            + (reasons.Length == 0 ? string.Empty : "Failures:\n  " + string.Join("\n  ", reasons) + "\n")
            + DebugFfmpegPlaytest.Verdict(didOpen, start, end, wall, reasons));
    }

    private static string DescribeFfmpegDecoder(DecoderEngine engine) => engine switch
    {
        DecoderEngine.SystemDecoder => "system",
        DecoderEngine.FFmpegSoftwareDecoder => "FFmpeg software",
        DecoderEngine.FFmpegD3D11HardwareDecoder => "FFmpeg D3D11",
        _ => "unknown",
    };

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
