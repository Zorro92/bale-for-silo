namespace Bale.Core.Tests;

/// <summary>
/// How the Xbox player wires in-band closed captions (PlayerPage.Captions.cs, docs/closed-captions.md): a decoder and token
/// per FFmpeg mount, a CC row per channel found, a client-only pick that switches the mount's decoder and remembers nothing, CC
/// kept across remounts on the picked channel, the overlay on the mount's sample timeline, and the debug readout. The pages compile only on Windows, so their wiring is
/// pinned by source here; the decisions are PlaybackClosedCaptionsTests, the decoder its Rust tests.
/// </summary>
public class PlayerClosedCaptionsWiringTests
{
    private static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, name)).ReplaceLineEndings("\n");

    private static string Section(string text, string from, string to)
    {
        var start = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, from);
        var end = text.IndexOf(to, start, StringComparison.Ordinal);
        Assert.True(end > start, to);
        return text[start..end];
    }

    private static void InOrder(string text, params string[] parts)
    {
        var at = -1;
        foreach (var part in parts)
        {
            var next = text.IndexOf(part, at + 1, StringComparison.Ordinal);
            Assert.True(next > at, "out of order or missing: " + part);
            at = next;
        }
    }

    /// <summary>The lines of <paramref name="text"/> that are not comments.</summary>
    private static string Code(string text) =>
        string.Join('\n', text.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)
            && !line.TrimStart().StartsWith("///", StringComparison.Ordinal)));

    [Fact]
    public void EveryFfmpegMountGetsItsOwnDecoderAndTokenAndDisposesThemWithIt()
    {
        var media = Read("PlayerPage.Media.cs.txt");
        var open = Section(media, "private async Task OpenMountAsync(", "private async Task<bool> MountEngineAsync(");
        // After the transport opened (an HLS mount's clock starts at its first segment) and before the FFmpeg source exists,
        // so the source is created with the mount's bale_cc_sink.
        InOrder(open,
            "transport.StartPlayerClockAt(hls.SegmentStartSeconds);",
            "FfmpegSource.ApplySubtitleSink(config, media.SubtitleStreamIndex, media.SubtitleSinkToken);",
            "StartCaptions(attempt, media, config);",
            "await FFmpegMediaSource.CreateFromInputStreamAsync(forward, config).AsTask()",
            "media.Source = MediaSource.CreateFromStream(media.RandomStream, transport.MimeType ?? \"video/mp4\");",
            "StartCaptions(attempt, media, null);");
        Assert.DoesNotContain("TODO(captions)", media);

        var captions = Read("PlayerPage.Captions.cs.txt");
        InOrder(Section(captions, "private void StartCaptions(", "private async Task AttachCaptionsAsync("),
            "var draw = attempt.Captions.PlanPlays(attempt.Plan);",
            "if (config is null || !PlaybackClosedCaptions.Taps(media.Route))",
            "if (ClosedCaptions.Create() is not { } decoder) return;",
            "var token = NextCaptionToken();",
            "if (decoder.Register(token) < 0 || decoder.Select(attempt.Captions.Channel) < 0)",
            "decoder.Dispose();",
            "media.CaptionDecoder = decoder;",
            "media.CaptionToken = token;",
            "FfmpegSource.ApplyCaptionSink(config, token);",
            "if (draw) _ = AttachCaptionsAsync(attempt, media);",
            "catch (Exception ex) { LogFailure(\"closed captions\", ex); }");
        // A fresh token from the fork's range for every mount, never reused while an older source may still close.
        Assert.Contains("private static long s_captionToken;", captions);
        Assert.Contains("(ulong)(Interlocked.Increment(ref s_captionToken) % (long)ClosedCaptions.MaxToken) + 1", captions);

        // Disposed with the mount on the UI thread, before the native sources close on the pool.
        InOrder(Section(media, "private void DisposeMount(Attempt attempt)", "private Task _mountClose"),
            "Cleanup(\"dispose closed captions\", () => media.CaptionDecoder?.Dispose());",
            "media.CaptionDecoder = null;",
            "media.CaptionsAttached = false;",
            "_mountClose = _mountClose.ContinueWith(");
    }

    [Fact]
    public void ChannelsAreOfferedFromTheTickAndListedLastInBothMenus()
    {
        InOrder(Section(Read("PlayerPage.cs.txt"), "private async void OnTick(", "if (_recording || _seeking) return;"), "UpdateCaptions();");
        Assert.Contains("public PlaybackClosedCaptions Captions { get; } = new();", Read("PlayerPage.cs.txt"));
        var captions = Read("PlayerPage.Captions.cs.txt");
        // Until every channel was found (a channel can start later in the stream), about once a second.
        InOrder(Section(captions, "private void UpdateCaptions()", "#if DEBUG_SERVER"),
            "attempt.Captions.AllFound", "if (now - _captionsCheckedAt < CaptionsCheckMs) return;",
            "var found = attempt.Captions.Observe(decoder.Seen());", "if (found == 0) return;",
            "Log.Info($\"player: closed captions found channel={PlaybackClosedCaptions.Names(found)}\");", "UpdateTrackValues();");

        // Both the control bar's menu and the Options panel read TrackOptions.
        var tracks = Read("PlayerPage.Tracks.cs.txt");
        Assert.Contains("return kind == PlaybackSelectorKind.Subtitles && _attempt is { } attempt ? attempt.Captions.WithEntries(options) : options;",
            Section(tracks, "private IReadOnlyList<PlaybackSelectorOption> TrackOptions(", "private void UpdateTrackValues()"));
        Assert.Contains("PlaybackOptionsTabs.TrackRows(TrackOptions(kind))", Read("PlayerPage.Options.cs.txt"));
    }

    [Fact]
    public void PickingCcIsClientOnlyAndRemembersNothing()
    {
        var tracks = Read("PlayerPage.Tracks.cs.txt");
        // Both menus route subtitle rows through the captions' pick.
        Assert.Contains("_ = kind == PlaybackSelectorKind.Audio ? ChangeTracksAsync(index, null) : PickSubtitleAsync(index);",
            Section(tracks, "private void OnTrackChosen(", "private async Task ChangeTracksAsync("));
        Assert.Contains("ChangeTracksAsync(trackIndex, null) : PickSubtitleAsync(trackIndex)", Read("PlayerPage.Options.cs.txt"));

        var captions = Read("PlayerPage.Captions.cs.txt");
        InOrder(Section(captions, "private Task PickSubtitleAsync(int index)", "private async Task PickClosedCaptionsAsync(uint channel)"),
            "if (PlaybackClosedCaptions.ChannelOf(index) is { } channel) return PickClosedCaptionsAsync(channel);",
            "if (_attempt is { } attempt && attempt.Captions.PickTrack(index))",
            "DetachCaptions(attempt.Media);",
            "return ChangeTracksAsync(null, index);");
        var pick = Section(captions, "private async Task PickClosedCaptionsAsync(uint channel)", "private void UpdateCaptions()");
        // A server subtitle goes Off first, as a step that is no preference; otherwise CC attaches at once. No replan for CC.
        InOrder(pick,
            "var serverSubtitle = attempt.Captions.Pick(channel, attempt.Plan);",
            "if (serverSubtitle)",
            "await ChangeTracksAsync(null, -1, remember: false);",
            "if (!attempt.Captions.PlanPlays(attempt.Plan))",
            "await AttachCaptionsAsync(attempt, media);",
            "UpdateTrackValues();");
        foreach (var server in new[] { "ReplanAsync", "PlaybackReplan.", "_trackPicks", "SaveTrackPicks", "SaveTrackPreferenceAsync" })
            Assert.DoesNotContain(server, pick);

        // The Off a CC pick sends stages no pick, so its plan saves nothing.
        var change = Section(tracks, "private async Task ChangeTracksAsync(", "private bool TrySwitchAudioLocally(");
        InOrder(change, "private async Task ChangeTracksAsync(int? audioIndex, int? subtitleIndex, bool remember = true)",
            "pick = !remember ? null", "if (pick is not null)\n            {\n                _trackPicks.Picked(pick);");

        // Owner, 2026-11-25: captions keep their own 608 colours and italics, in the subtitle appearance; never plain.
        // One overlay track change at a time: a sidecar of an earlier plan still parsing goes first, and a newer clear or
        // mount owns the overlay instead.
        InOrder(Section(captions, "private async Task AttachCaptionsAsync(", "private static void DetachCaptions("),
            "var generation = _subtitleGeneration;", "await _subtitleLoads.WaitAsync(token);",
            "if (generation != _subtitleGeneration || !attempt.Captions.Selected || !ReferenceEquals(attempt.Media, media)",
            "var overlay = EnsureSubtitleOverlay();", "var hr = overlay.AttachCaptions(decoder, SubtitleStyle(attempt.Client));",
            "media.CaptionsAttached = true;", "finally", "if (holdsLoad) _subtitleLoads.Release();");
        Assert.DoesNotContain("plain", Code(captions));
        Assert.Contains("Cleanup(\"detach closed captions\", () => decoder.Detach());", captions);
    }

    [Fact]
    public void APickSwitchesTheMountsDecoderAndEveryNewDecoderStartsOnThePickedChannel()
    {
        var captions = Read("PlayerPage.Captions.cs.txt");
        // Only a channel found on this attempt; the same channel again changes nothing.
        var pick = Section(captions, "private async Task PickClosedCaptionsAsync(uint channel)", "private void UpdateCaptions()");
        InOrder(pick,
            "|| !attempt.Captions.Offers(channel)) return;",
            "if (attempt.Captions.Selected && attempt.Captions.Channel == channel)",
            "var serverSubtitle = attempt.Captions.Pick(channel, attempt.Plan);",
            // bale_cc_select on the playing mount's decoder at once, before the Off path or the attach.
            "media.CaptionDecoder?.Select(channel);",
            "if (serverSubtitle)",
            "await AttachCaptionsAsync(attempt, media);");
        // A remount's new decoder decodes the attempt's channel before the tap's first packet (the sink is set after it).
        InOrder(Section(captions, "private void StartCaptions(", "private async Task AttachCaptionsAsync("),
            "decoder.Select(attempt.Captions.Channel)", "FfmpegSource.ApplyCaptionSink(config, token);");
        Assert.Contains("Log.Info($\"player: subtitles mode=render format=cea608 channel=cc{attempt.Captions.Channel}\");", captions);

        // The decoder side: another channel starts that channel's decoder afresh and drops the previous channel's cues and the
        // attached overlay's events (a Clear), so nothing drawn from the previous channel stays up; the same channel is kept.
        InOrder(Read("bale_gpu_cc_mod.rs.txt"),
            "pub fn select(&self, channel: u32, service: u32) -> i32 {", "if inner.channel != channel {",
            "inner.decoder = Decoder::new(channel);", "inner.forget();", "inner.post();",
            "fn forget(&mut self) {", "self.history.clear();", "self.commands.push(LiveCommand::Clear);");
        Assert.Contains("internal int Select(uint channel) => Call(\"select\", captions => bale_cc_select(captions, channel, 0));",
            Read("ClosedCaptions.cs.txt"));
    }

    [Fact]
    public void AnAnswerAdoptedInPlaceDecidesAndEveryRemountKeepsCc()
    {
        // In place: the answer's server subtitle ends CC, an Off keeps it, drawn again on the cleared overlay.
        InOrder(Section(Read("PlayerPage.Tracks.cs.txt"), "private bool TryAdoptSubtitleInPlace(", "private void SaveTrackPicks("),
            "attempt.Plan = answer;", "ClearSubtitles();", "StartSubtitles(attempt, media);", "AdoptCaptions(attempt, media);");
        InOrder(Section(Read("PlayerPage.Captions.cs.txt"), "private void AdoptCaptions(", "private Task PickSubtitleAsync("),
            "if (attempt.Captions.PlanPlays(attempt.Plan)) _ = AttachCaptionsAsync(attempt, media);", "else DetachCaptions(media);");

        // A recovery replan, seek reanchor, HLS remount or quality change mounts through MountPlanAsync, whose OpenMountAsync
        // starts the new mount's captions (above); the attempt's choice is what survives, not the mount's decoder.
        var media = Read("PlayerPage.Media.cs.txt");
        InOrder(Section(media, "private async Task MountPlanAsync(", "private async Task OpenMountAsync("),
            "ClearSubtitles();", "try { await OpenMountAsync(attempt, media, transport, playerStart, paused, rangeRefused); }");
    }

    [Fact]
    public void CaptionsRunOnTheMountsSampleTimelineAndASeekDropsTheirEvents()
    {
        // Cue times are the tap's MediaStreamSample timeline, the player clock: the overlay's origin is the mount's offset.
        InOrder(Section(Read("PlayerPage.Captions.cs.txt"), "private async Task AttachCaptionsAsync(", "private static void DetachCaptions("),
            "_subtitleTimingOrigin = media.Transport.Timeline.TimelineOffsetSeconds;", "UpdateSubtitleLayout();",
            "PushSubtitleClock(force: true);");
        Assert.Contains("overlay.SetClock(SubtitleClockPush.Milliseconds(position - _subtitleTimingOrigin), rate, playing);",
            Read("PlayerPage.Subtitles.cs.txt"));
        // A seek inside the mount: the fork calls bale_cc_flush, which drops what is held and clears the overlay's events.
        InOrder(Read("bale_gpu_cc_mod.rs.txt"),
            "pub fn flush(&self) -> i32 {", "inner.restart();",
            "fn restart(&mut self) {", "self.reorder.flush();", "self.forget();",
            "fn forget(&mut self) {", "self.commands.push(LiveCommand::Clear);");
    }

    [Fact]
    public void TheDebugReadoutAndPickAreWired()
    {
        var server = Read("DebugServer.cs.txt");
        InOrder(server, "builder.Append(\"Skip markers: \").Append(player.DebugSkipMarkers).Append('\\n');",
            "builder.Append(\"Captions: \").Append(player.DebugCaptions).Append('\\n');");
        InOrder(server, "case (\"GET\", \"/captions\"):", "return await CaptionsAsync(null);",
            "case (\"POST\", \"/captions\"):", "query.GetValueOrDefault(\"action\") is not (\"cc\" or \"cc1\" or \"cc2\" or \"cc3\" or \"cc4\" or \"off\")",
            "return await CaptionsAsync(query[\"action\"]);");
        Assert.Contains("or \"/media-fault\" or \"/captions\" or", server);
        Assert.Contains("GET /captions, POST /captions?action={cc|cc1|cc2|cc3|cc4|off}", server);
        Assert.Contains("POST /captions?action=cc|cc1|cc2|cc3|cc4|off\\n\"", Read("DebugServer.Captions.cs.txt"));
        Assert.Contains("player.DebugPickCaptionsAsync(action)", Read("DebugServer.Captions.cs.txt"));

        var captions = Read("PlayerPage.Captions.cs.txt");
        var readout = Section(captions, "internal string DebugCaptions", "private static string SeenText(");
        foreach (var field in new[] { "offered=", "picked=", "attached=", "channel=", "token=", "seen=", "packets=", "captioned=", "cues=", "late=", "flushes=", "origin=" })
            Assert.Contains(field, readout);
        // The offered channels and the picked one, by name.
        Assert.Contains("offered={(captions.Discovered ? PlaybackClosedCaptions.Names(captions.Found) : \"no\")}", readout);
        Assert.Contains("picked={(captions.Selected ? $\"cc{captions.Channel}\" : \"no\")}", readout);
        // The debug pick takes the menu's own path: cc is CC1, cc1..cc4 each channel, only once found on this playback.
        var debugPick = Section(captions, "internal async Task<(string Status, string Body)> DebugPickCaptionsAsync(", "#endif");
        InOrder(debugPick, "var channel = DebugCaptionChannel(action);", "!attempt.Captions.Offers(wanted)", "\"409 Conflict\"",
            "await PickSubtitleAsync(channel is { } row ? PlaybackClosedCaptions.TrackIndexOf(row) : -1);",
            "\"cc\" or \"cc1\" => 1u,", "\"cc2\" => 2u,", "\"cc3\" => 3u,", "\"cc4\" => 4u,", "_ => null,");

        var script = Read("bale-debug.sh");
        Assert.Contains("#   scripts/bale-debug.sh captions [cc|cc1|cc2|cc3|cc4|off]", script);
        Assert.Contains("! \"$2\" =~ ^(cc[1-4]?|off)$", script);
        Assert.Contains("curl --fail-with-body -sS --max-time 5 \"$BASE/captions\"", script);
        Assert.Contains("curl --fail-with-body -sS --max-time 45 -X POST -G \"$BASE/captions\" --data-urlencode \"action=$2\"", script);
    }
}
