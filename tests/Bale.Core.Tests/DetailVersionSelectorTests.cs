using Bale.Core.Api.Models;
using Bale.Core.Detail;

namespace Bale.Core.Tests;

/// <summary>
/// DetailVersionSelectorTest.kt: Edition is the cut and Version Auto picks inside it. Official Auto
/// (no display match); Bale's display matching inside an edition is covered at the end.
/// </summary>
public class DetailVersionSelectorTests
{
    // The issue's example: Theatrical in 4K and 1080p, Extended in 1080p only.
    private static readonly FileVersion Theatrical4K = File("1", "2160p", "theatrical", "Theatrical");
    private static readonly FileVersion Theatrical1080 = File("2", "1080p", "theatrical", "Theatrical");
    private static readonly FileVersion Extended1080 = File("3", "1080p", "extended", "Extended");
    private static readonly FileVersion[] Versions = [Extended1080, Theatrical1080, Theatrical4K];

    private static string? Select(string? pinned = null, string? edition = null, string? lastFile = null,
        string? lastEditionKey = null, bool inProgress = false, string? quality = "2160p",
        IReadOnlyList<FileVersion>? files = null, IReadOnlyList<PlaybackVariant>? variants = null,
        DisplayVersionMatch? match = null) =>
        PlaybackFormatting.SelectVersion(files ?? Versions, match, pinned, lastFile, quality, variants, edition,
            lastEditionKey, inProgress)?.FileId;

    [Fact]
    public void NothingPlayedOpensOnThePreferredCutWithItsBestFile() => Assert.Equal("1", Select());

    [Fact]
    public void APickedEditionKeepsAutoInsideItEvenWhenAnotherCutIsBetter() =>
        Assert.Equal("3", Select(edition: "extended"));

    [Fact]
    public void TheLastPlayedEditionOpensWithItsBestFileForThePreference()
    {
        Assert.Equal("3", Select(lastFile: "3"));
        // History names the cut, the preference names the file.
        Assert.Equal("1", Select(lastFile: "2"));
        Assert.Equal("3", Select(lastFile: "99", lastEditionKey: "extended"));
        Assert.Equal("1", Select(lastFile: "3", lastEditionKey: "theatrical"));
        Assert.Equal("3", Select(lastFile: "3", lastEditionKey: "imax"));
        // A present "" names the unnamed edition.
        Assert.Equal("5", Select(lastFile: "3", lastEditionKey: "", files: [.. Versions, File("5", "720p")]));
    }

    [Fact]
    public void AutoBreaksResolutionTiesOnHdrThenFileSizeNotBitrate()
    {
        var sdrBig = File("1", "1080p", size: 9_000, bitrate: 90_000);
        var hdrSmall = File("2", "1080p", hdr: true, size: 1_000);
        var sdrSmall = File("3", "1080p", size: 1_000, bitrate: 99_000);
        Assert.Equal("2", Select(files: [sdrBig, sdrSmall, hdrSmall]));
        Assert.Equal("1", Select(files: [sdrSmall, sdrBig]));
        Assert.Equal("3", Select(files: [sdrSmall, File("1", "1080p", size: 1_000, bitrate: 90_000)]));
    }

    [Fact]
    public void AnUnknownResolutionIsALastResort()
    {
        var unknown = File("1", null, size: 9_000);
        var above = File("2", "2160p");
        Assert.Equal("2", Select(files: [unknown, above], quality: "1080p"));
        Assert.Equal("2", Select(files: [unknown, above], quality: "auto"));
        Assert.Equal("1", Select(files: [unknown], quality: "1080p"));
    }

    [Fact]
    public void WithNoHistoryTheCutIsPickedByNameThenQuality()
    {
        // DetailVersionSelectorTest.kt (Android e15879514; Apple b54fefb1, Roku 3e7ee3a, server 66105aa).
        static FileVersion Cut(string id, string? key, string resolution, bool hdr = false, long size = 1) =>
            File(id, resolution, key, key, hdr, size);
        // Name first (unnamed or standard, then theatrical, then any other cut), even over better quality.
        Assert.Equal("1", Select(files: [Cut("1", "standard", "1080p"), Cut("2", "extended", "2160p", hdr: true)]));
        Assert.Equal("2", Select(files: [Cut("1", "theatrical", "1080p"), Cut("2", null, "1080p")]));
        Assert.Equal("2", Select(files: [Cut("1", "theatrical", "1080p"), Cut("2", "standard edition", "1080p")]));
        Assert.Equal("2", Select(files: [Cut("1", "directors_cut", "1080p", size: 9_000), Cut("2", "theatrical", "1080p")]));
        // Among equally named cuts, a file within the cap beats one only above it.
        Assert.Equal("2", Select(quality: "1080p", files: [Cut("1", "imax", "2160p"), Cut("2", "extended", "720p")]));
        // Above the cap, the closer one wins.
        Assert.Equal("2", Select(quality: "480p", files: [Cut("1", "imax", "2160p"), Cut("2", "extended", "720p")]));
        // Same resolution: HDR; never file size.
        Assert.Equal("2", Select(files: [Cut("1", "extended", "1080p", size: 9_000), Cut("2", "directors_cut", "1080p", hdr: true)]));
        // A full tie keeps server order.
        Assert.Equal("1", Select(files: [Cut("1", "extended", "1080p"), Cut("2", "directors_cut", "1080p", size: 9_000)]));
        // A last played edition still wins over the name.
        Assert.Equal("2", Select(lastEditionKey: "extended", files: [Cut("1", "standard", "1080p"), Cut("2", "extended", "2160p", hdr: true)]));
    }

    [Fact]
    public void APinWins()
    {
        Assert.Equal("2", Select(pinned: "2"));
        Assert.Equal("2", Select(pinned: "2", edition: "theatrical"));
    }

    [Fact]
    public void WithNoFileAtThePreferenceAutoTakesTheClosestAbove()
    {
        var theatrical720 = File("4", "720p", "theatrical", "Theatrical");
        Assert.Equal("4", Select(edition: "theatrical", quality: "480p", files: [.. Versions, theatrical720]));
        Assert.Equal("2", Select(edition: "theatrical", quality: "720p"));
        // A preference that names no resolution reads as Auto.
        Assert.Equal("1", Select(edition: "theatrical", quality: "high"));
    }

    [Fact]
    public void AutoRanksEveryPresentationOfTheCut()
    {
        var single4K = File("20", "2160p", "final_cut", "Final Cut");
        var cd1 = Part(File("11", "1080p", "final_cut", "Final Cut"), 1);
        var cd2 = Part(File("12", "1080p", "final_cut", "Final Cut"), 2);
        PlaybackVariant[] variants =
        [
            new() { VariantId = "final_cut|single", EditionKey = "final_cut", Parts = [new() { PartIndex = 1, Versions = [Ref("20")] }] },
            new() { VariantId = "final_cut|multipart", EditionKey = "final_cut", Parts =
                [new() { PartIndex = 1, Versions = [Ref("11")] }, new() { PartIndex = 2, Versions = [Ref("12")] }] },
        ];
        FileVersion[] files = [cd1, cd2, single4K];
        Assert.Equal("20", Select(lastFile: "11", files: files, variants: variants));
        Assert.Equal("12", Select(lastFile: "12", inProgress: true, files: files, variants: variants));
        var edition = Assert.Single(PlaybackEditions.For(files, variants));
        Assert.Equal(new[] { "11", "20" }, edition.Versions.Select(file => file.FileId));
        Assert.True(edition.FileIds.SetEquals(["11", "12", "20"]));
    }

    [Fact]
    public void ResumeStaysOnTheLaterPartOfAMultiPartEdition()
    {
        var part1 = Part(File("10", "1080p"), 1);
        var part2 = Part(File("11", "1080p"), 2);
        PlaybackVariant[] variants =
        [
            new() { VariantId = "standard", Parts =
                [new() { PartIndex = 1, Versions = [Ref("10")] }, new() { PartIndex = 2, Versions = [Ref("11")] }] },
        ];
        Assert.Equal("11", Select(lastFile: "11", inProgress: true, files: [part1, part2], variants: variants));
        Assert.Equal("11", Select(lastFile: "11", inProgress: true, edition: "", files: [part1, part2], variants: variants));
        Assert.Equal("10", Select(files: [part1, part2], variants: variants));
        Assert.Equal("11", Select(lastFile: "11", inProgress: true, files: [part1, part2]));
        var biggerPart2 = Part(File("11", "1080p", size: 9_000), 2);
        Assert.Equal("10", Select(files: [part1, biggerPart2]));
        // Finished (no saved position): a rewatch starts at part 1.
        Assert.Equal("10", Select(lastFile: "11", files: [part1, part2]));
    }

    [Fact]
    public void DisplayMatchingRanksInsideTheEditionOnly()
    {
        DisplayVersionMatch match = new(1080, [new("hevc", "test", [], [], [8, 10], 3840, 2160, 60, null, true)]);
        var extended4K = File("4", "2160p", "extended", "Extended");
        FileVersion[] files = [Extended1080, Theatrical1080, Theatrical4K, extended4K];
        // A 1080p display picks the edition's 1080p file, never another cut's.
        Assert.Equal("3", Select(edition: "extended", quality: null, files: files, match: match));
        Assert.Equal("2", Select(lastFile: "1", quality: null, files: files, match: match));
        // Original ignores the display and takes the edition's best.
        Assert.Equal("4", Select(edition: "extended", quality: "original", files: files, match: match));
    }

    [Fact]
    public void DetailOverloadUsesTheItemsHistoryAndResumePoint()
    {
        var detail = Movie(Versions) with { LastFileId = "3", LastEditionKey = null };
        Assert.Same(Extended1080, PlaybackFormatting.SelectDisplayVersion(detail, PlaybackSelection.Auto, "2160p"));
        Assert.Same(Theatrical4K, PlaybackFormatting.SelectDisplayVersion(detail with { LastEditionKey = "theatrical" },
            PlaybackSelection.Auto, "2160p"));
        Assert.Same(Theatrical4K, PlaybackFormatting.SelectDisplayVersion(detail,
            new PlaybackSelection(EditionId: "theatrical"), "2160p"));
    }

    private static FileVersion File(string id, string? resolution, string? key = null, string? raw = null,
        bool hdr = false, long? size = null, long? bitrate = null) =>
        new() { FileId = id, Resolution = resolution, EditionKey = key, EditionRaw = raw, Hdr = hdr, FileSize = size, Bitrate = bitrate };

    private static FileVersion Part(FileVersion file, long index)
    {
        file.PresentationKind = "multipart_movie";
        file.PresentationPartIndex = index;
        return file;
    }

    private static FileVersion Ref(string id) => new() { FileId = id };

    internal static ItemDetail Movie(IReadOnlyList<FileVersion> versions) =>
        new("movie", "movie", "Movie", null, null, null, null, null, [], null, [], [], null, [], null, "movie", null, null, new(false, false, false), [])
        { Versions = versions };
}
