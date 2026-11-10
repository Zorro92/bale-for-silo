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
        var start = PlaybackStartBuilder.Build(detail, new PlaybackSelection(options.FileId, SubtitleIndex: options.SubtitleIndex), profile.Id,
            Guid.NewGuid().ToString(), null, options.Capabilities(), identity);
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
                $" overlay={(client.Playback.ResolveSubtitles(plan) is { } overlay ? overlay.FormatName : "none")}");
            foreach (var row in subtitle?.Inventory ?? [])
                Console.WriteLine($"  subtitle #{row.CombinedIndex} {row.TrackId} codec={row.Codec} lang={row.Language} source={row.Source}" +
                    $" delivery={row.Delivery} forced={row.Forced} default={row.Default}" +
                    $" sidecar={(row.Url is { Length: > 0 } ? "yes" : "no")} fonts={(row.FontBundleUrl is { Length: > 0 } ? "yes" : "no")}");

            if (options.UrlFile is { } path)
            {
                if (sessionId is not { Length: > 0 } || plan.Delivery != "original_http"
                    || PlaybackMediaUrl.Resolve(stream.Url, client.Server) is not { } url)
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

    internal static void PrintFailure(string step, Exception ex) => Console.Error.WriteLine(
        $"{step} failed hresult=0x{unchecked((uint)ex.HResult):X8} status={(ex as Microsoft.Kiota.Abstractions.ApiException)?.ResponseStatusCode}");
}
