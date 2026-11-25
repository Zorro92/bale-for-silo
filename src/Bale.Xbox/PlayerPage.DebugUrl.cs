#if DEBUG_SERVER
using Bale.Core.Playback;
using Bale.Xbox.Services;

namespace Bale.Xbox;

public sealed partial class PlayerPage
{
    private string _debugUrlDiagnostics = "";

    private async Task StartDebugUrlAsync(Attempt attempt)
    {
        try
        {
            var url = attempt.DebugUrl!;
            var (capabilities, softwareVideo) = await PlaybackClaims.DetectAsync();
            if (attempt.Av1ClaimMaxHeight == 2160 && (attempt.Av1Decoder == "rav1g" || attempt.Av1ClaimDav1d) && softwareVideo)
                capabilities = capabilities with { VideoDecode = ConsoleEvidence.BuildVideoDecode(await HardwareDecodeProbe.Limits, 3840, 2160) };
            capabilities = DebugFfmpegPlayback.Capabilities(attempt.FfmpegMode, capabilities);
            capabilities = DebugHdrPlayback.Capabilities(attempt.PretendHdr, capabilities);
            capabilities = attempt.FfmpegTuning.ToneMapCapabilities(capabilities);
            attempt.Cancellation.Token.ThrowIfCancellationRequested();
            attempt.HardwareDecode = capabilities.VideoDecode.Where(decode => decode.Hardware).ToArray();
            attempt.Start = PlaybackStartBuilder.Build(DebugPlayUrlRequest.FileId, attempt.Client.ProfileId!,
                Guid.NewGuid().ToString(), attempt.Request.OverridePosition, new(DebugPlayUrlRequest.FileId),
                capabilities, DeviceIdentity.Current());
            attempt.Plan = url.Plan();
            attempt.PlanAttemptId = Guid.NewGuid().ToString();
            attempt.Position = _sourcePosition = attempt.Request.OverridePosition ?? 0;
            // Attached means the local pipeline is active too; no reporter, journal, session id or Silo call exists.
            attempt.Attached = true;
            AutoPlayRow.IsEnabled = false;
            _debugUrlDiagnostics = $"Debug file: [redacted-url]; no Silo session; av1-claim={attempt.Av1ClaimMaxHeight}; server claim limits are not negotiated\n";
            Log.Info("player: debug file [redacted-url]; no Silo session; probing through the normal range reader");
            await MountPlanAsync(attempt, attempt.Position);
        }
        catch (Exception) when (attempt.Cancellation.IsCancellationRequested) { }
        catch (DebugPlayUrlException ex) { await DebugUrlFailedAsync(ex.Message, ex.HResult); }
        catch (Exception ex)
        {
            LogFailure("debug file open", ex);
            await DebugUrlFailedAsync(attempt.Media?.StartBudgetRanOut == true
                ? "playback start budget expired" : "file open or playback failed; no server recovery", ex.HResult);
        }
    }

    private async Task<Mount> ProbeDebugUrlAsync(Attempt attempt, double playerStart, bool paused)
    {
        var url = attempt.DebugUrl!;
        var media = attempt.Media = new Mount(PlaybackMedia.OpenDebugFile(url.Url, attempt.Plan!), attempt.Cancellation.Token)
        {
            StartPosition = playerStart, StartPaused = paused, StartBudget = NewStartBudget(),
            DebugHang = DebugStartBudget.TakeHang(),
            Route = new(false, false, null, null, null, null, null),
        };
        // The normal prefetch/cache sizes, one stream reused at open, with no second reader or retained decoded frames.
        var prefetch = attempt.FfmpegTuning.Prefetch ?? HttpRangeStream.DefaultPrefetchBlocks;
        media.Stream = attempt.UseRustTransport
            ? await OpenStepAsync(media.Transport.OpenNativeStreamAsync(media.Token, prefetch), media.Token)
            : await OpenStepAsync(media.Transport.OpenStreamAsync(media.Token, prefetch), media.Token);
        FfmpegLogging.Configure(); // Native libavformat logging must go through the same redacting filter.
        var probe = await Task.Run(() => DebugFileProbeReader.Read(media.Stream, media.Token));
        media.Token.ThrowIfCancellationRequested();
        attempt.Request = attempt.Request with { Detail = url.Detail(probe) };
        attempt.Plan = url.Plan(probe);
        media.Transport = PlaybackMedia.OpenDebugFile(url.Url, attempt.Plan);
        _debugUrlDiagnostics += "Debug file: " + probe.Describe() + "\n";
        Log.Info("player: debug file " + probe.Describe());
        return media;
    }

    private static void DebugValidateUrlRoute(Attempt attempt, Mount media)
    {
        if (attempt.DebugUrl is null) return;
        // No server can transcode a debug file: a codec past the console's hardware limits (e.g. 4K H.264 on a 1080p-only
        // H.264 decoder) that Bale doesn't software-decode cannot play here; say so instead of a generic open failure.
        // Release decodes H.264/HEVC/VP9 such files inside the route's bounded software fallback (phase A); the debug
        // software fallback (ffmpeg-tune swfallback=on or decoder=software) decodes them past those bounds too.
        if (media.Route.SoftwareVideo && media.Route.VideoCodec is { } codec && !SoftwareDecodePolicy.VideoCodecs.Contains(codec)
            && !media.Route.SoftwareFallback && !attempt.FfmpegTuning.SoftwareFallbackAdmits(media.Route))
            throw new DebugPlayUrlException(!SoftwareDecodePolicy.SoftwareFallbackVideoCodecs.Contains(codec)
                ? $"{codec} at this size/rate exceeds the console's hardware decoder and Bale does not software-decode {codec}; no server here to transcode"
                : media.Route.ConsoleStage
                    ? $"{codec} past the console's hardware decoder needs FFmpeg software, and the console stage takes only d3d11va frames; no server here to tone-map"
                    : $"{codec} is past the console's hardware decoder and outside Bale's software envelope (1920x1080; 4:2:2/4:4:4 up to 30 fps; see docs/software-decode.md) (debug: ffmpeg-tune swfallback=on); no server here to transcode");
        if (media.Route.ConsoleToneMap && !media.Route.UseFfmpeg)
            throw new DebugPlayUrlException("tone map declined: FFmpeg is disabled or the codec is unsupported");
        if (media.Route.ConsoleStage && !ConsoleToneMapRuntime.IsAvailable)
            throw new DebugPlayUrlException("console stage declined: runtime is unavailable");
        if (media.Route.ConsoleToneMap && media.Route.VideoCodec is not ("hevc" or "vp9")
            && !(media.Route.VideoCodec == "av1" && attempt.Av1Decoder == "rav1g"))
            throw new DebugPlayUrlException("tone map declined: requires HEVC/VP9 hardware or rav1g AV1");
    }

    private void DebugDescribeUrlRoute(Attempt attempt, Mount media)
    {
        if (attempt.DebugUrl is null) return;
        var route = $"route={(media.Route.SoftwareVideo ? "software" : "system")} codec={media.Route.VideoCodec} stage={media.Route.ConsoleStage}";
        if (media.Route.SoftwareFallback)
            route += "; software fallback (past the hardware decoder, inside the release envelope)";
        else if (attempt.FfmpegTuning.SoftwareFallbackAdmits(media.Route))
            route += "; debug software fallback (past the release envelope; release builds refuse it)";
        if (media.Route.VideoCodec == "av1")
        {
            route += !attempt.FfmpegTuning.SoftwareVideo(media.Route.SoftwareVideo) ? "; rav1g not applied: ffmpeg-tune decoder override"
                : attempt.Av1Decoder != "rav1g" ? "; rav1g not selected (av1-decoder dav1d)"
                : media.Rav1g?.Rav1g == true ? "; rav1g requested (no server fps/size claim gate)"
                : "; rav1g declined: route is not software AV1 or decoder was withdrawn";
        }
        _debugUrlDiagnostics += "Debug file: " + route + "\n";
        Log.Info("player: debug file " + route);
    }

    private async Task DebugUrlFailedAsync(string reason, int hresult)
    {
        // Only fixed local diagnostics or numeric FFmpeg/HRESULT values, never arbitrary transport exception prose.
        var message = $"Debug file: {reason}; hr=0x{unchecked((uint)hresult):X8}";
        if (_attempt is { DebugUrl: not null, Av1Decoder: "rav1g", Media: { Route.VideoCodec: "av1" } })
        {
            var (_, _, fallback, failed, _) = DebugRav1gLog.Snapshot();
            if (fallback is not null || failed is not null) message += $"; rav1g={failed ?? fallback}";
        }
        Log.Info("player: " + message);
        _debugNavigationFailure = ("409 Conflict", message + "\n");
        await ShowErrorAsync(message);
    }
}
#endif
