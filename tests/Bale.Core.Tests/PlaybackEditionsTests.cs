using Bale.Core.Api.Models;
using Bale.Core.Detail;

namespace Bale.Core.Tests;

/// <summary>PlaybackEditionsTest.kt / TvPlaybackSelectorRow.kt: editions own files, not quality labels.</summary>
public class PlaybackEditionsTests
{
    [Theory]
    [InlineData(" International ", "ignored", "International")]
    [InlineData(" ", " imax_extended-cut ", "IMAX Extended Cut")]
    [InlineData(null, "__imax--extended_cut__", "IMAX Extended Cut")]
    [InlineData(null, "director's_cut", "Director's Cut")]
    [InlineData(null, " ", null)]
    [InlineData(null, null, null)]
    public void LabelsPreferTrimmedRawThenHumanizeTheKey(string? raw, string? key, string? expected) =>
        Assert.Equal(expected, PlaybackEditions.Label(raw, key));

    [Fact]
    public void NamedCutsKeepTheirFilesAndCatalogOrderEvenWithIdenticalQuality()
    {
        var theatrical = File("10", "theatrical", "Theatrical");
        var international = File("20", "international", "International");
        var international4k = File("30", "international", "International", "2160p");
        var editions = PlaybackEditions.For([theatrical, international, international4k]);
        Assert.True(PlaybackEditions.HasChoices(editions));
        Assert.Equal(new[] { "Theatrical", "International" }, editions.Select(edition => edition.Label));
        Assert.Equal(new[] { "10" }, editions[0].Versions.Select(file => file.FileId));
        Assert.Equal(new[] { "20", "30" }, editions[1].Versions.Select(file => file.FileId));
        Assert.Same(editions[1], PlaybackEditions.Current(editions, international4k));
        Assert.Same(editions[0], PlaybackEditions.Current(editions, new FileVersion { FileId = "unknown" }));
    }

    [Fact]
    public void MissingAndKeyOnlyLabelsRemainUsableWithOneEditionPerKey()
    {
        var standard = new FileVersion { FileId = "1" };
        var editions = PlaybackEditions.For([standard, File("2", "imax_extended-cut", " ")]);
        Assert.Equal(new[] { "Standard", "IMAX Extended Cut" }, editions.Select(edition => edition.Label));
        Assert.True(PlaybackEditions.HasChoices(editions));
        Assert.False(PlaybackEditions.HasChoices(PlaybackEditions.For([standard, new FileVersion { FileId = "3" }])));
        Assert.Empty(PlaybackEditions.For([]));
        Assert.Null(PlaybackEditions.Current([], null));
    }

    [Fact]
    public void VariantsOfferTheFirstPartsFilesAndPreserveCatalogObjectsAcrossAllParts()
    {
        var first = File("10");
        var preferred = File("20");
        var later = File("30");
        PlaybackVariant variant = new()
        {
            VariantId = "international|single", EditionKey = "international", EditionRaw = " International ", DefaultFileId = "30",
            Parts =
            [
                new() { PartIndex = 2, DefaultFileId = "30", Versions = [new() { FileId = "30" }, new() { FileId = "40" }] },
                new() { PartIndex = 1, DefaultFileId = "20", Versions = [new() { FileId = "10" }, new() { FileId = "20" }] },
            ],
        };
        var edition = Assert.Single(PlaybackEditions.For([later, preferred, first], [variant]));
        Assert.Equal("international", edition.Id);
        Assert.Equal("International", edition.Label);
        // Server order: the order of the catalog's versions, not of the part's list.
        Assert.Equal(new[] { preferred, first }, edition.Versions);
        Assert.True(edition.FileIds.SetEquals(["10", "20", "30", "40"]));
        Assert.Same(edition, PlaybackEditions.Current([edition], later));
    }

    [Fact]
    public void VariantsAreAuthoritativeAndDoNotFallBackToAnotherPartOrUngroupedFiles()
    {
        var file = File("10", "theatrical", "Theatrical");
        PlaybackVariant unplayable = new()
        {
            Parts =
            [
                new() { PartIndex = 2, Versions = [file] },
                new() { PartIndex = 1, Versions = [new() { FileId = "missing" }] },
            ],
        };
        Assert.Empty(PlaybackEditions.For([file], [new PlaybackVariant(), unplayable]));
    }

    [Fact]
    public void UnknownFilesAreSkippedAndMissingIndexesMeanZero()
    {
        var stale = File("10");
        var current = File("10", "file-key", "File label");
        var other = File("20");
        PlaybackVariant variant = new()
        {
            VariantId = "server-group", EditionKey = "imax", Parts =
            [
                new() { PartIndex = 1, Versions = [other] },
                new() { DefaultFileId = "missing", Versions = [new() { FileId = "unknown" }, stale, other] },
            ],
        };
        var edition = Assert.Single(PlaybackEditions.For([stale, other, current], [variant]));
        Assert.Equal("IMAX", edition.Label);
        Assert.Equal(new[] { "20", "10" }, edition.Versions.Select(file => file.FileId));
        Assert.Same(current, edition.Versions[1]);
    }

    [Fact]
    public void OneKeyShowsNoEditionAndOffersEveryFileUnderAuto()
    {
        var detail = Movie([File("10", "one", "Theatrical"), File("20", " one ", "THEATRICAL")]);
        Assert.False(PlaybackEditions.HasChoices(PlaybackEditions.For(detail.Versions)));
        Assert.False(PlaybackFormatting.SelectorIsInteractive(PlaybackSelectorKind.Edition, detail, PlaybackSelection.Auto));
        Assert.True(PlaybackFormatting.SelectorIsInteractive(PlaybackSelectorKind.Version, detail, PlaybackSelection.Auto));
        var options = PlaybackFormatting.SelectorOptions(PlaybackSelectorKind.Version, detail, PlaybackSelection.Auto);
        Assert.Equal(new string?[] { null, "10", "20" }, options.Select(option => option.FileId));
        Assert.Equal("Auto", Assert.Single(options, option => option.IsSelected).Title);
    }

    [Fact]
    public void ScopedVersionOptionsKeepAutoAndEditionRowsShowTheirAutoFile()
    {
        var detail = Movie([File("10", "theatrical", "Theatrical"), File("20", "international", "International"),
            File("30", "international", "International", "2160p")]) with { LastFileId = "20" };
        Assert.True(PlaybackFormatting.SelectorIsInteractive(PlaybackSelectorKind.Edition, detail, PlaybackSelection.Auto));
        Assert.True(PlaybackFormatting.SelectorIsInteractive(PlaybackSelectorKind.Version, detail, PlaybackSelection.Auto));
        var options = PlaybackFormatting.SelectorOptions(PlaybackSelectorKind.Version, detail, PlaybackSelection.Auto);
        Assert.Equal(new string?[] { null, "20", "30" }, options.Select(option => option.FileId));
        Assert.Equal("Auto", Assert.Single(options, option => option.IsSelected).Title);
        var editionOptions = PlaybackFormatting.SelectorOptions(PlaybackSelectorKind.Edition, detail, PlaybackSelection.Auto);
        Assert.Equal(new[] { "Theatrical", "International" }, editionOptions.Select(option => option.Title));
        Assert.Equal("international", Assert.Single(editionOptions, option => option.IsSelected).EditionId);
        // History picks the cut; Auto picks its best file (4K here, with no display match).
        Assert.Equal("4K", editionOptions[1].Detail);
        Assert.Equal("30", Assert.Single(PlaybackFormatting.SelectorOptions(PlaybackSelectorKind.Version, detail,
            new PlaybackSelection("30")), option => option.IsSelected).FileId);
        Assert.Equal("Auto · 4K", PlaybackSummaryValues.For(detail).Version);
    }

    [Fact]
    public void SingleFileEditionKeepsVersionNonInteractiveDespiteOtherEditionsFiles()
    {
        var detail = Movie([File("10", "theatrical", "Theatrical"), File("20", "international", "International")]);
        Assert.True(PlaybackFormatting.SelectorIsInteractive(PlaybackSelectorKind.Edition, detail, PlaybackSelection.Auto));
        Assert.False(PlaybackFormatting.SelectorIsInteractive(PlaybackSelectorKind.Version, detail, PlaybackSelection.Auto));
        Assert.Equal(new string?[] { null, "10" }, PlaybackFormatting.SelectorOptions(PlaybackSelectorKind.Version, detail,
            PlaybackSelection.Auto).Select(option => option.FileId));
        Assert.False(PlaybackFormatting.SelectorIsInteractive(PlaybackSelectorKind.Edition, null, PlaybackSelection.Auto));
        Assert.False(PlaybackFormatting.SelectorIsInteractive(PlaybackSelectorKind.Edition, detail with { Versions = [] }, PlaybackSelection.Auto));
    }

    [Fact]
    public void LaterPartIdentifiesEditionButQualityOptionsStayOnItsFirstPart()
    {
        var detail = Movie([File("10"), File("20"), File("30"), File("40")]) with
        {
            LastFileId = "30",
            PlaybackVariants =
            [
                new() { VariantId = "standard", Parts = [new() { Versions = [new() { FileId = "40" }] }] },
                new() { VariantId = "extended", EditionKey = "extended", EditionRaw = "Extended", Parts =
                [
                    new() { PartIndex = 2, Versions = [new() { FileId = "30" }] },
                    new() { PartIndex = 1, DefaultFileId = "20", Versions = [new() { FileId = "10" }, new() { FileId = "20" }] },
                ] },
            ],
        };
        var edition = Assert.Single(PlaybackFormatting.SelectorOptions(PlaybackSelectorKind.Edition, detail, PlaybackSelection.Auto), option => option.IsSelected);
        Assert.Equal("Extended", edition.Title);
        Assert.Equal("extended", edition.EditionId);
        var options = PlaybackFormatting.SelectorOptions(PlaybackSelectorKind.Version, detail, PlaybackSelection.Auto);
        Assert.Equal(new string?[] { null, "10", "20" }, options.Select(option => option.FileId));
        Assert.Equal("Auto", Assert.Single(options, option => option.IsSelected).Title);
    }

    [Fact]
    public void EditionPicksKeepVersionOnAutoResetTracksAndRemainEpisodeScoped()
    {
        var first = File("10", "theatrical", "Theatrical");
        first.AudioTracks = [new() { Language = "eng" }];
        var detail = Movie([first, File("20", "extended", "Extended"), File("30", "extended", "Extended", "2160p")])
            with { Type = "episode", LastFileId = "10" };
        var state = new PlaybackSelectionState();
        state.Select(detail, PlaybackSelectorKind.Audio, new("English", "", true, TrackIndex: 0));
        state.Select(detail, PlaybackSelectorKind.Subtitles, new("Off", "", true, TrackIndex: -1));
        var edition = PlaybackFormatting.SelectorOptions(PlaybackSelectorKind.Edition, detail, state.For(detail))[1];
        var picked = new PlaybackSelection(EditionId: "extended");
        Assert.Equal(picked, state.Select(detail, PlaybackSelectorKind.Edition, edition));
        Assert.Equal("30", PlaybackFormatting.SelectDisplayVersion(detail, picked)?.FileId);
        Assert.Equal(PlaybackSelection.Auto, state.For(detail with { ContentId = "another-episode" }));
        Assert.Equal(picked, state.For(detail));
        // A Version pick inside the edition keeps the edition.
        var version = PlaybackFormatting.SelectorOptions(PlaybackSelectorKind.Version, detail, picked)[1];
        Assert.Equal(new PlaybackSelection("20", EditionId: "extended"), state.Select(detail, PlaybackSelectorKind.Version, version));
        // An edition the item no longer has falls back to history.
        Assert.Null(state.For(detail with { Versions = [first] }).EditionId);
    }

    private static FileVersion File(string id, string? key = null, string? raw = null, string resolution = "1080p") =>
        new() { FileId = id, EditionKey = key, EditionRaw = raw, Resolution = resolution };

    private static ItemDetail Movie(IReadOnlyList<FileVersion> versions) =>
        new("movie", "movie", "Movie", null, null, null, null, null, [], null, [], [], null, [], null, "movie", null, null, new(false, false, false), [])
        { Versions = versions };
}
