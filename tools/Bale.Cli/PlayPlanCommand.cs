using System.Globalization;
using Bale.Core;
using Bale.Core.Connection;
using Bale.Core.Detail;
using Bale.Core.Playback;

internal static class PlayPlanCommand
{
    internal static async Task<int> RunAsync(SiloClient client, ClientIdentity identity, string contentId,
        PlayPlanOptions options, CancellationToken cancellationToken)
    {
        var profile = (await client.GetProfilesAsync(cancellationToken)).FirstOrDefault(p => p.Name == "Bale Test");
        if (profile?.Id is null) { Console.Error.WriteLine("No profile named Bale Test."); return 1; }
        client.UseProfile(profile.Id);
        await client.GetAccountAsync(cancellationToken);
        var capability = await client.Playback.GetCapabilitiesAsync(cancellationToken);
        var installation = new CliInstallation(capability.InstallationId!);
        var detail = await client.Detail.LoadAsync(contentId, cancellationToken);
        var start = options.Apply(PlaybackStartBuilder.Build(detail, new PlaybackSelection(options.FileId, SubtitleIndex: options.SubtitleIndex), profile.Id,
            Guid.NewGuid().ToString(), options.StartSeconds, options.Capabilities(), identity));
        var decision = await client.Playback.StartAsync(start, installation, cancellationToken);
        var sessionId = decision.SessionId;
        // Process-local probe: no shared temporary journal to collide with another held CLI session.
        var reporter = client.Playback.CreateProgressReporter(installation, new InMemoryPlaybackJournal(), "bale-cli");
        using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? heartbeatTask = null;
        try
        {
            if (sessionId is { Length: > 0 })
            {
                await reporter.AttachSessionAsync(sessionId, 0, true);
                heartbeatTask = reporter.RunAsync(sessionId, heartbeat.Token);
            }
            // Server problem messages and stream URL/header shapes can contain grants. Print codes only.
            Console.WriteLine($"outcome={decision.Outcome} terminal={decision.Terminal?.Reason}");
            var plan = decision.PlaybackPlan;
            if (plan?.Stream is not { } stream) return 1;
            Console.WriteLine($"delivery={plan.Delivery} protocol={stream.Protocol} container={stream.Container} mime={stream.MimeType}");
            Console.WriteLine($"reason={plan.DecisionReason} quirks=[{string.Join(",", plan.AppliedQuirks?.Select(q => q.Id) ?? [])}] warnings=[{string.Join(",", plan.DegradationWarnings?.Select(w => w.Code) ?? [])}]");
            var source = plan.Source;
            var recipe = plan.EffectiveRecipe;
            Console.WriteLine($"source container={source?.Container} video={source?.VideoCodec}" +
                $" profile={source?.VideoProfile} level={source?.VideoLevel} bits={source?.BitDepth}" +
                $" {source?.Width}x{source?.Height}@{source?.FrameRate} range={source?.DynamicRange}" +
                $" kbps={source?.BitrateKbps} audio={source?.AudioCodec} ch={source?.AudioChannels} layout={source?.AudioLayout}");
            Console.WriteLine($"recipe video={recipe?.VideoCodec} sample={recipe?.VideoSampleEntry}" +
                $" {recipe?.Width}x{recipe?.Height} audio={recipe?.AudioCodec} ch={recipe?.AudioChannels}" +
                $" seg={recipe?.SegmentContainer} kbps={recipe?.BitrateKbps} quality={plan.ActiveQuality}" +
                $" corrections=[{string.Join(",", plan.RuntimeCorrections ?? [])}]");
            Console.WriteLine($"timeline source={plan.Timeline?.SourceStartSeconds} player={plan.Timeline?.PlayerStartSeconds} offset={plan.Timeline?.TimelineOffsetSeconds} duration={plan.Source?.DurationSeconds}");
            // Shapes only: subtitle and font URLs carry the session, so print whether they exist, never them.
            var subtitle = plan.Subtitle;
            Console.WriteLine($"subtitle mode={subtitle?.Mode} track={subtitle?.TrackId} format={subtitle?.Artifact?.Format}" +
                $" mime={subtitle?.Artifact?.MimeType} origin={subtitle?.Artifact?.TimingOriginSeconds}" +
                $" artifact={(subtitle?.Artifact?.Url is { Length: > 0 } ? "yes" : "no")} embedded={(subtitle?.Embedded is null ? "no" : "yes")}" +
                $" overlay={(client.Playback.ResolveSubtitles(plan) is { } overlay ? overlay.FormatName
                    : client.Playback.ResolveEmbeddedSubtitles(plan) is { } bitmap ? $"bitmap stream={bitmap.StreamIndex}" : "none")}");
            foreach (var row in subtitle?.Inventory ?? [])
                Console.WriteLine($"  subtitle #{row.CombinedIndex} {row.TrackId} codec={row.Codec} lang={row.Language} source={row.Source}" +
                    $" delivery={row.Delivery} forced={row.Forced} default={row.Default}" +
                    $" sidecar={(row.Url is { Length: > 0 } ? "yes" : "no")} fonts={(row.FontBundleUrl is { Length: > 0 } ? "yes" : "no")}");

            if (options.InspectHls) await InspectHlsAsync(client, plan, cancellationToken);
            var planAttemptId = Guid.NewGuid().ToString();
            foreach (var target in options.ReanchorSeconds ?? [])
            {
                if (sessionId is not { Length: > 0 } || decision.ServerFeatures?.Contains(PlaybackSeekReanchor.Feature) != true) return 1;
                // Like the player, seeks keep the current plan, tracks and recovery count (one
                // on a fresh session). This command never mounts media or exercises Xbox APIs.
                var replan = PlaybackReplan.ForSeek(start, plan, planAttemptId, [], 1, target);
                decision = await client.Playback.ReplanAsync(sessionId, replan, installation, cancellationToken);
                if (!PlaybackSeekReanchor.Matches(plan, decision, sessionId, start.FileId))
                {
                    Console.Error.WriteLine("reanchor rejected: response changed route identity or was not playable");
                    return 1;
                }
                plan = decision.PlaybackPlan!;
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"reanchor target={target:0.###} same_route=yes attempt_count={replan.AttemptCount}"
                    + $" source={plan.Timeline?.SourceStartSeconds:0.###} player={plan.Timeline?.PlayerStartSeconds:0.###} offset={plan.Timeline?.TimelineOffsetSeconds:0.###}"));
                if (options.InspectHls) await InspectHlsAsync(client, plan, cancellationToken);
            }

            if (options.UrlFile is { } path)
            {
                if (sessionId is not { Length: > 0 } || plan.Delivery != "original_http"
                    || PlaybackMediaUrl.Resolve(plan.Stream?.Url, client.Server) is not { } url)
                {
                    Console.Error.WriteLine("url not written: no playable original_http session");
                    return 1;
                }
                await PlayPlanUrlFile.WriteAsync(path, url, cancellationToken);
                Console.WriteLine("url written");
            }
            if (options.SaveHead is { } headPath)
            {
                if (sessionId is not { Length: > 0 } || plan.Delivery != "original_http")
                {
                    Console.Error.WriteLine("head not saved: no playable original_http session");
                    return 1;
                }
                var saved = await PlayPlanHeadFile.SaveAsync(client.Playback.OpenMedia(plan), headPath,
                    options.HeadMegabytes * 1024L * 1024, cancellationToken);
                Console.WriteLine($"head saved bytes={saved}");
            }
            if (options.SaveRange is { } rangePath)
            {
                if (sessionId is not { Length: > 0 } || plan.Delivery != "original_http")
                {
                    Console.Error.WriteLine("range not saved: no playable original_http session");
                    return 1;
                }
                var saved = await PlayPlanHeadFile.SaveAsync(client.Playback.OpenMedia(plan), rangePath,
                    options.RangeOffset, options.RangeBytes, cancellationToken);
                Console.WriteLine($"range saved bytes={saved}");
            }
            if (options.HoldSeconds > 0)
            {
                if (sessionId is not { Length: > 0 }) return 1;
                await Task.Delay(TimeSpan.FromSeconds(options.HoldSeconds), cancellationToken);
            }
            return 0;
        }
        finally
        {
            await heartbeat.CancelAsync();
            if (heartbeatTask is not null)
            {
                try { await heartbeatTask; }
                catch (OperationCanceledException) { }
                catch (Exception ex) { PrintFailure("heartbeat", ex); }
            }
            if (sessionId is { Length: > 0 })
            {
                // Ctrl+C cancels the hold, not the final stop. Do not record probe wall time as watched time.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var stopAt = double.TryParse(Environment.GetEnvironmentVariable("BALE_STOP_AT"), CultureInfo.InvariantCulture, out var at)
                    && double.IsFinite(at) && at >= 0 ? at : 0;
                try
                {
                    await reporter.StopAsync(sessionId, stopAt, true, cleanup.Token);
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"session stopped at {stopAt}"));
                }
                catch (Exception ex) { PrintFailure("stop", ex); }
            }
        }
    }

    private static async Task InspectHlsAsync(SiloClient client, Bale.Core.Api.Models.PlaybackPlan plan, CancellationToken ct)
    {
        var media = client.Playback.OpenMedia(plan);
        if (!media.IsHls) throw new InvalidOperationException("HLS inspection requires an HLS plan.");
        var uri = media.Uri;
        for (var depth = 0; depth < 2; depth++)
        {
            var (data, _) = await media.GetBytesAsync(uri, null, null, ct);
            var lines = System.Text.Encoding.UTF8.GetString(data).Split('\n').Select(line => line.Trim()).ToArray();
            if (lines.Any(line => line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal)))
            {
                uri = new Uri(uri, lines.First(line => line.Length > 0 && !line.StartsWith('#')));
                continue;
            }
            // Shape and finite numbers only; manifests contain signed segment/key URIs.
            var type = lines.Contains("#EXT-X-PLAYLIST-TYPE:EVENT") ? "EVENT" : lines.Contains("#EXT-X-PLAYLIST-TYPE:VOD") ? "VOD" : "unspecified";
            var durations = lines.Where(line => line.StartsWith("#EXTINF:", StringComparison.Ordinal))
                .Select(line => double.TryParse(line[8..].Split(',')[0], CultureInfo.InvariantCulture, out var seconds)
                    && double.IsFinite(seconds) && seconds > 0 ? seconds : 0).ToArray();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"hls type={type} endlist={lines.Contains("#EXT-X-ENDLIST")} start_tag={lines.Any(line => line.StartsWith("#EXT-X-START:", StringComparison.Ordinal))}"
                + $" init_map={lines.Any(line => line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))} segments={durations.Length} produced_seconds={durations.Sum():0.###}"));
            return;
        }
        throw new InvalidDataException("HLS media playlist unavailable.");
    }

    internal static void PrintFailure(string step, Exception ex) => Console.Error.WriteLine(
        $"{step} failed hresult=0x{unchecked((uint)ex.HResult):X8} status={(ex as Microsoft.Kiota.Abstractions.ApiException)?.ResponseStatusCode}"
        + (ex is Bale.Core.Api.Models.Problem problem ? " " + PlaybackProblemLog.Describe(problem) : ""));
}
