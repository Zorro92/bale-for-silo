using Bale.Core.Api.Models;
using Bale.Core.Detail;

namespace Bale.Core.Tests;

/// <summary>shared/src/commonTest/kotlin/org/siloserver/silo/model/playback/SeriesAudioChoiceTest.kt.</summary>
public class SeriesAudioChoiceTests
{
    private static AudioTrack Main => new() { Language = "eng", Codec = "truehd", Channels = 8, Layout = "7.1", Title = "English Atmos" };
    private static AudioTrack Commentary => new() { Language = "eng", Codec = "ac3", Channels = 2, Layout = "stereo", Title = "Commentary" };
    private static AudioTrack Japanese => new() { Language = "jpn", Codec = "aac", Channels = 2, Layout = "stereo", EmbeddedTitle = "Stereo" };

    [Fact]
    public void SignatureWinsAcrossIsoLanguageFormsThenPositionThenLanguage()
    {
        var commentary = Commentary;
        commentary.Language = "en";
        var choice = Choice([Main, commentary], 1);
        Assert.Equal(0, Resolve(choice, [Commentary, Main, Japanese]));
        Assert.Equal(1, Resolve(choice, [Japanese, Main]));
        choice.AudioTrackIndex = 0;
        Assert.Equal(1, Resolve(choice, [Japanese, Main]));
        Assert.Null(Resolve(choice, [Japanese]));
    }

    [Fact]
    public void AChoiceMovesToItsLanguageOnAnotherVersionInsteadOfReusingItsPosition()
    {
        var choice = Choice([Main, Japanese], 0);
        Assert.Equal(1, Resolve(choice, [Japanese, new() { Language = "en", Codec = "aac", Channels = 2 }]));
    }

    [Fact]
    public void AnUntaggedPickIsFoundBySignatureOnlyAmongUntaggedTracks()
    {
        var untagged = new AudioTrack { Language = "und", Codec = "aac", Channels = 2, Layout = "stereo", Title = "Commentary" };
        var choice = Choice([Main, untagged], 1);
        Assert.Null(choice.AudioLanguage);
        var mix = new AudioTrack { Codec = "aac", Channels = 2, Layout = "stereo", Title = "Stereo Mix" };
        untagged.Language = null;
        Assert.Equal(1, Resolve(choice, [mix, untagged]));
        untagged.Language = "eng";
        Assert.Null(Resolve(choice, [untagged]));
        Assert.Null(Resolve(new() { TrackSignature = new() }, [new()]));
    }

    [Fact]
    public void ExactRegionalTagThenBareLanguageThenOtherVariantBreakSignatureAndLanguageTies()
    {
        var brazil = new AudioTrack { Language = "pt-BR", Codec = "aac", Channels = 2, Title = "Portuguese" };
        var portugal = new AudioTrack { Language = "pt-PT", Codec = "aac", Channels = 2, Title = "Portuguese" };
        var bare = new AudioTrack { Language = "por", Codec = "aac", Channels = 2, Title = "Portuguese" };
        var choice = Choice([brazil], 0);
        Assert.Equal(1, Resolve(choice, [portugal, brazil]));
        choice.TrackSignature = null;
        choice.AudioTrackIndex = 9;
        Assert.Equal(1, Resolve(choice, [portugal, bare]));
        Assert.Equal(1, Resolve(choice, [bare, brazil]));
        Assert.Equal(0, Resolve(choice, [portugal]));
        choice.AudioTrackIndex = 0;
        Assert.Equal(0, Resolve(choice, [portugal, brazil]));
    }

    [Fact]
    public void UnknownCodecsAreExcludedUnlessNoTrackCanBeDecoded()
    {
        var brazil = new AudioTrack { Language = "pt-BR", Codec = "aac", Channels = 2, Title = "Portuguese" };
        var portugal = new AudioTrack { Language = "pt-PT", Codec = "aac", Channels = 2, Title = "Portuguese" };
        var choice = Choice([brazil], 0);
        brazil.Codec = "unknown";
        Assert.Equal(1, Resolve(choice, [brazil, portugal]));
        Assert.Equal(0, Resolve(choice, [brazil]));
        brazil.Codec = null;
        Assert.Equal(1, Resolve(choice, [brazil, portugal]));
    }

    [Theory]
    [InlineData("title")]
    [InlineData("embeddedTitle")]
    [InlineData("codec")]
    [InlineData("layout")]
    [InlineData("channels")]
    public void SignatureRequiresAllNonLanguageFieldsToMatch(string changedField)
    {
        var choice = Choice([Commentary], 0);
        // No language fallback: this specifically exercises an exact untagged signature.
        choice.AudioLanguage = null;
        choice.TrackSignature!.Language = null;
        var track = Commentary;
        track.Language = null;
        switch (changedField)
        {
            case "title": track.Title = "Main"; break;
            case "embeddedTitle": track.EmbeddedTitle = "Other"; break;
            case "codec": track.Codec = "aac"; break;
            case "layout": track.Layout = "5.1"; break;
            case "channels": track.Channels = 6; break;
        }
        Assert.Null(Resolve(choice, [track]));
    }

    [Fact]
    public void SignatureTextIgnoresCaseAndWhitespaceButDefaultIsNotPartOfTheMatch()
    {
        var choice = Choice([Main, Commentary], 1);
        var track = Commentary;
        track.Title = "  COMMENTARY  ";
        track.Codec = " AC3 ";
        track.Default = true;
        Assert.Equal(0, Resolve(choice, [track, Main]));
    }

    [Fact]
    public void PendingChoiceOverlaysEachVersionsEffectiveOrdinalAndAutoClearsTheOldServerChoice()
    {
        var first = new FileVersion { AudioTracks = [Main, Japanese], EffectiveAudioTrackIndex = 0 };
        var second = new FileVersion { AudioTracks = [Japanese, Main], EffectiveAudioTrackIndex = 1 };
        var pending = new PendingSeriesAudioChoice(PlaybackTrackChoices.Audio(first, 1));
        Assert.Equal(1, PlaybackAudioSelection.AutomaticAudioOrdinal(first, "en", null, pending));
        Assert.Equal(0, PlaybackAudioSelection.AutomaticAudioOrdinal(second, "en", null, pending));
        Assert.Null(PlaybackAudioSelection.AutomaticAudioOrdinal(first, "en", null, new(null)));
        Assert.Null(PlaybackAudioSelection.AutomaticAudioOrdinal(second, "en", null, new(null)));
        Assert.Equal(0, PlaybackAudioSelection.AutomaticAudioOrdinal(first, "en", new(["aac"]), new(null)));
        Assert.Equal(1, PlaybackAudioSelection.AutomaticAudioOrdinal(second, "en", new(["aac"]), new(null)));
        Assert.Equal(0, PlaybackAudioSelection.AutomaticAudioOrdinal(first, "ja", null));
        Assert.Equal(1, PlaybackAudioSelection.AutomaticAudioOrdinal(second, "ja", null));
        Assert.Equal(0, first.EffectiveAudioTrackIndex);
        Assert.Equal(1, second.EffectiveAudioTrackIndex);
    }

    [Fact]
    public void MissingPendingTrackClearsServerChoiceAndFallsBackToTheDeviceLanguage()
    {
        var pending = new PendingSeriesAudioChoice(Choice([Japanese], 0));
        var version = new FileVersion
        {
            AudioTracks = [new() { Language = "en", Codec = "aac" }, new() { Language = "es", Codec = "ac3" }],
            EffectiveAudioTrackIndex = 1,
        };
        Assert.Null(PlaybackAudioSelection.AutomaticAudioOrdinal(version, "en", null, pending));
        Assert.Equal(0, PlaybackAudioSelection.AutomaticAudioOrdinal(version, "en", new(["aac", "ac3"]), pending));
        Assert.Null(Resolve(pending.Choice!, []));
        Assert.Null(PlaybackAudioSelection.AutomaticAudioOrdinal(null, "en", new(["aac"]), pending));
    }

    private static AudioPreferenceUpdate Choice(List<AudioTrack> tracks, int ordinal) =>
        PlaybackTrackChoices.Audio(new FileVersion { AudioTracks = tracks }, ordinal);

    private static int? Resolve(AudioPreferenceUpdate choice, IReadOnlyList<AudioTrack> tracks) =>
        PlaybackTrackChoices.ResolveAudioOrdinal(choice, tracks);
}
