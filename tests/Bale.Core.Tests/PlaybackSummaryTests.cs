using Bale.Core.Api.Models;
using Bale.Core.Detail;

namespace Bale.Core.Tests;

public class PlaybackSummaryTests
{
    [Fact]
    public void MovieWithOneVersionUsesAutoAndDoesNotGuessAudioCapabilities()
    {
        var movie = Item("m");
        Assert.Equal(new PlaybackSummaryValues("Auto · 4K · HDR", "Auto", "English · SRT"), PlaybackSummaryValues.For(movie));
        movie.Versions[0].EffectiveAudioTrackIndex = 0;
        Assert.Equal("Auto", PlaybackSummaryValues.For(movie).Audio);
    }

    [Fact]
    public void MovieWithSeveralVersionsUsesLastPlayedThenBestQuality()
    {
        var movie = Item("m") with { Versions = [new FileVersion { FileId = "1080", Resolution = "1080p" }, .. Item("m").Versions] };
        Assert.Equal("Auto · 4K · HDR", PlaybackSummaryValues.For(movie).Version);
        Assert.Equal("Auto · 1080P", PlaybackSummaryValues.For(movie with { LastFileId = "1080" }).Version);
        Assert.Equal("Auto · 1080P", PlaybackSummaryValues.For(movie, preferredQuality: "1080p").Version);
    }

    [Fact]
    public void LaterExplicitSelectionsHaveNoAutoAnnotations()
    {
        var values = PlaybackSummaryValues.For(Item("m"), "file", 0, -1);
        Assert.Equal(new PlaybackSummaryValues("4K · HDR", "English · EAC3 · 5.1", "Off"), values);
        Assert.Equal("Auto · 4K · HDR", PlaybackSummaryValues.For(Item("m"), "missing").Version);
    }

    [Fact]
    public void UnknownAudioPreventsAnUnfoundedSubtitleAutoPreview()
    {
        var movie = Item("m") with { EffectiveSubtitleMode = "off" };
        Assert.Equal("English · SRT", PlaybackSummaryValues.For(movie).Subtitles);
        Assert.Equal("Auto · None", PlaybackSummaryValues.For(movie, automaticAudioResolutionKnown: true).Subtitles);
        Assert.Equal("Auto · None", PlaybackSummaryValues.For(movie, selectedAudioTrackIndex: 0).Subtitles);
    }

    [Fact]
    public void ServerSubtitlePreferencesAndEffectiveAudioBeatProfileFallbacks()
    {
        var movie = Item("m") with { EffectiveSubtitleLanguage = "en", EffectiveSubtitleMode = "always" };
        movie.Versions[0].AudioTracks!.Add(new AudioTrack { Language = "fre" });
        movie.Versions[0].EffectiveAudioTrackIndex = 1;
        var values = PlaybackSummaryValues.For(movie, automaticAudioResolutionKnown: true, automaticAudioTrackOrdinal: 0,
            subtitleAutoContext: new SubtitleAutoContext("fr", "off"));
        Assert.Equal("Auto · French", values.Audio);
        Assert.Equal("Auto · English · SRT", values.Subtitles);
    }

    [Fact]
    public void MissingVersionRendersSkeletonsButMissingMetadataUsesAuto()
    {
        Assert.Equal(PlaybackSummaryValues.Empty, PlaybackSummaryValues.For(null));
        Assert.Equal(PlaybackSummaryValues.Empty, PlaybackSummaryValues.For(Item("m") with { Versions = [] }));
        Assert.Equal(new PlaybackSummaryValues("Auto", "Auto", "Auto"), PlaybackSummaryValues.For(Item("m") with { Versions = [new FileVersion()] }));
    }

    [Theory]
    [InlineData("series")]
    [InlineData("season")]
    public void ContainerReadoutUsesOnlyThePlayEpisodesIdentityCheckedDetail(string type)
    {
        var container = Item("s", type);
        var state = new PlaybackSummaryState();
        Assert.Equal(PlaybackSummaryValues.Empty, state.Update(container));
        Assert.Equal(PlaybackSummaryValues.Empty, state.Update(container, "e1", Item("e2", "episode")));
        Assert.Equal(PlaybackSummaryValues.For(Item("e1")), state.Update(container, "e1", Item("e1", "episode")));
    }

    [Fact]
    public void NextUpRetainsSettledValuesUntilLoadingEndsAndNeverLeaksAcrossTitles()
    {
        var state = new PlaybackSummaryState();
        var series = Item("s", "series");
        Assert.Equal(PlaybackSummaryValues.Empty, state.Update(series, "e1", transitioning: true));
        var first = state.Update(series, "e1", Item("e1"));
        Assert.Equal(first, state.Update(series, "e2", transitioning: true));
        var second = Item("e2") with { Versions = [new FileVersion { Resolution = "1080p" }] };
        Assert.Equal(first, state.Update(series, "e2", second, transitioning: true));
        Assert.Equal("Auto · 1080P", state.Update(series, "e2", second).Version);
        Assert.Equal(PlaybackSummaryValues.Empty, state.Update(series, "e3"));
        Assert.Equal(PlaybackSummaryValues.Empty, state.Update(Item("other", "series"), transitioning: true));
        Assert.Equal(PlaybackSummaryValues.For(Item("movie")), state.Update(Item("movie"), transitioning: true));
    }

    private static ItemDetail Item(string id, string type = "movie") =>
        new(id, type, "Title", null, null, null, null, null, [], null, [], [], null, [], null, id, null, null, new(false, false, false), [])
        {
            Versions =
            [
                new FileVersion
                {
                    FileId = "file", Resolution = "2160p", Hdr = true,
                    AudioTracks = [new AudioTrack { Language = "eng", Codec = "eac3", Layout = "5.1" }],
                    SubtitleTracks = [new VersionSubtitleTrack { Language = "eng", Codec = "subrip" }],
                },
            ],
        };
}
