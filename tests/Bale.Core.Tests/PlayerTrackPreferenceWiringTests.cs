using Bale.Core.Detail;
using Bale.Core.Playback;
using Bale.Xbox.Services;

namespace Bale.Core.Tests;

/// <summary>
/// How the Xbox player and Detail page use <see cref="PlayerTrackPreferences"/> (TvPlayerViewModel.kt: a pick is saved
/// once its change commits; silo-roku 3ae62aa: Detail reads its choices again after such a player). The pages compile
/// only on Windows, so their wiring is pinned by source here; the decisions are PlayerTrackPreferencesTests.
/// </summary>
public class PlayerTrackPreferenceWiringTests
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

    [Fact]
    public void APickWaitsForAPlanThatPlaysItAndAChangeThatEndsBeforeOneIsAdoptedDropsIt()
    {
        var tracks = Read("PlayerPage.Tracks.cs.txt");
        // The checked row comes through too: a pick of what plays is saved at once, without a replan.
        var chosen = Section(tracks, "private void OnTrackChosen(", "private async Task ChangeTracksAsync(");
        Assert.DoesNotContain("IsSelected", chosen);
        var change = Section(tracks, "private async Task ChangeTracksAsync(", "private bool TrySwitchAudioLocally(");
        // A busy player keeps no pick: it is staged only once the change owns the player.
        InOrder(change,
            "if (!_lifecycle.Wait(0))",
            "_trackPicks.Picked(pick);",
            "if (attempt.Plan is { } playing && PlayerTrackPreferences.Plays(pick, playing, attempt.Start!.FileId))\n                {\n                    adopted = true;\n                    SaveTrackPicks(attempt);\n                    return;",
            "if (TrySwitchAudioLocally(attempt, media, audioIndex, subtitleIndex))\n            {\n                adopted = true;",
            "attempt.Client.Playback.ReplanAsync(",
            "Log.Info(\"player: track change refused; playback continues unchanged\");\n                return;",
            "if (TryAdoptSubtitleInPlace(attempt, media, decision, request, audioIndex))\n            {\n                adopted = true;",
            "attempt.Plan = decision.PlaybackPlan!;\n            // Saved when the replacement opens, if the answer plays the pick (OnMediaOpened, SaveTrackPicks).\n            adopted = true;",
            "await MountPlanAsync(",
            "finally\n        {\n            // A refused, failed or abandoned change never makes its pick the viewer's choice; a newer pick waits on.\n            if (pick is not null && !adopted) _trackPicks.Refused(pick);");

        // The local audio switch and the in-place subtitle answer are plans that play the pick.
        InOrder(Section(tracks, "private bool TrySwitchAudioLocally(", "private bool TryAdoptSubtitleInPlace("),
            "attempt.Start = PlaybackReplan.WithLocalAudio(attempt.Start!, attempt.Plan!, index);", "SaveTrackPicks(attempt);");
        InOrder(Section(tracks, "private bool TryAdoptSubtitleInPlace(", "private void SaveTrackPicks("),
            "attempt.Plan = answer;", "StartSubtitles(attempt, media);", "SaveTrackPicks(attempt);");

        // A save never fails playback, tells the Detail page underneath, and outlives the player: no attempt token.
        var save = tracks[tracks.IndexOf("private void SaveTrackPicks(", StringComparison.Ordinal)..];
        InOrder(save, "try", "_trackPicks.Settle(attempt.Request.Detail, plan, attempt.Start?.FileId)",
            "TrackChoiceReturn.Record(write);", "_ = SaveTrackPreferenceAsync(attempt.Client.Detail, write, kind);",
            "catch (Exception ex) { LogFailure(\"track preference\", ex); }", "await feed.SaveTrackPreferenceAsync(write);");
        Assert.DoesNotContain("Cancellation", save);
    }

    [Fact]
    public void APlanThatOpensSavesThePicksItPlaysAndANewAttemptForgetsThem()
    {
        var media = Read("PlayerPage.Media.cs.txt");
        var opened = Section(media, "private void OnMediaOpened(", "// Also the in-player audio picker's local switch");
        // A mount whose embedded subtitle cannot be drawn replans first; it never counts as playing the pick.
        InOrder(opened, "if (RecoverEmbeddedSubtitle(attempt, media)) return;",
            "media.Opened = true;\n            Log.Info(\"player: opened\");", "SaveTrackPicks(attempt);", "media.Opening.TrySetResult(true);");

        var page = Read("PlayerPage.cs.txt");
        InOrder(Section(page, "private async Task StartAttemptAsync()", "private async Task AttachSessionAsync("),
            "_trackPicks.Clear();\n            var attempt = _attempt = new Attempt(request, client);");

        // Up Next lets a pick's write land before the next episode's detail and start (the server resolves an omitted
        // audio track from the saved preference).
        InOrder(Read("PlayerPage.UpNext.cs.txt"), "await attempt.Client.Detail.WhenTrackChoicesSavedAsync(DetailFeed.TrackChoiceSettleTimeout);",
            "var detail = await attempt.Client.Detail.GetAsync(next.ContentId);");

        // The Options panel's checked row comes through as well (it was skipped before).
        Assert.Contains("if (!rows.Any(row => row.TrackIndex == trackIndex && row.IsEnabled)) return;", Read("PlayerPage.Options.cs.txt"));
    }

    [Fact]
    public void DetailPutsTheSavedKindsBackOnAutoAndReadsAfterThePlayersWrites()
    {
        var refresh = Section(Read("DetailPage.Player.cs.txt"), "private async Task RefreshAfterPlaybackAsync(", "private async Task RefreshSeasonsAsync(");
        InOrder(refresh,
            "var tracks = TrackChoiceReturn.Take(PlaybackTrackChoices.OwnerId(owner));",
            "var trackRevision = _trackChoiceRevision;",
            "_playbackSelections.ForgetTrackPicks(tracks);",
            "if (tracks is not null) await client.Detail.WhenTrackChoicesSavedAsync(DetailFeed.TrackChoiceSettleTimeout);",
            "var refreshed = await client.Detail.GetAsync(owner.ContentId);",
            "if (tracks is not null && !owner.PlaysEpisodes && trackRevision == _trackChoiceRevision)",
            "Versions = refreshed.Versions,",
            "EffectiveSubtitleTrackSignature = refreshed.EffectiveSubtitleTrackSignature,",
            "_nextUpPlaybackTargetId = null;\n            ShowPlayback();");
    }

    [Fact]
    public void TheReturnIsTakenOnceAndOnlyByItsOwnItem()
    {
        var audio = new PlayerTrackPreferenceWrite("show", Audio: PlaybackTrackChoices.Audio(new() { AudioTracks = [new() { Language = "en" }] }, 0));
        TrackChoiceReturn.Record(audio);
        TrackChoiceReturn.Record(new PlayerTrackPreferenceWrite("show", Subtitle: PlaybackTrackChoices.SubtitleOff()));
        Assert.Equal(new PlayerTrackChoiceReturn("show", true, true), TrackChoiceReturn.Take("show"));
        Assert.Null(TrackChoiceReturn.Take("show"));
        // A page for another title finds nothing, and the stale return is gone for the right one too.
        TrackChoiceReturn.Record(audio);
        Assert.Null(TrackChoiceReturn.Take("movie"));
        Assert.Null(TrackChoiceReturn.Take("show"));
    }
}
