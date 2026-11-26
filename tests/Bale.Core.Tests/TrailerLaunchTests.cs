using Bale.Core.Api.Models;
using Bale.Core.Connection;
using Bale.Core.Detail;
using Bale.Core.Playback;

namespace Bale.Core.Tests;

/// <summary>
/// The Trailers &amp; More cards (TvDetailTrailersSection.kt, TvItemDetailScreen.kt:1448-1466, YoutubeTrailerLauncher.kt): a
/// local extra plays in Bale's player from its start, a YouTube trailer opens its public watch page, and nothing else is
/// ever started.
/// </summary>
public class TrailerLaunchTests
{
    [Fact]
    public void ALocalExtraPlaysByItsContentIdAndFile()
    {
        Assert.Equal(new ExtraPlayback("local-0a1b2c", "91"), TrailerLaunch.For(Card(new LocalExtraSource(" local-0a1b2c ", "91"))));
        Assert.Equal(new ExtraPlayback("movie-tmdb-1", null), TrailerLaunch.For(Card(new LocalExtraSource("movie-tmdb-1", null))));
        // A kind:id form (the Android tests' "extra:1") is still one path segment.
        Assert.Equal(new ExtraPlayback("extra:1", null), TrailerLaunch.For(Card(new LocalExtraSource("extra:1", null))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData("https://example.org/extra.mp4")]
    [InlineData("//example.org/extra.mp4")]
    [InlineData("/api/v2/media/1")]
    [InlineData("a/../b")]
    [InlineData("a\\b")]
    [InlineData("a?b=1")]
    [InlineData("a#b")]
    [InlineData("a%2Fb")]
    [InlineData("a b")]
    public void AnExtraThatIsNoServerContentIdStartsNothing(string contentId)
    {
        Assert.Null(TrailerLaunch.ServerContentId(contentId));
        Assert.Null(TrailerLaunch.For(Card(new LocalExtraSource(contentId, "1"))));
    }

    [Fact]
    public void AYouTubeVideoOpensItsPublicWatchPage()
    {
        var link = Assert.IsType<TrailerLink>(TrailerLaunch.For(Card(new RemoteVideoSource("YouTube", "abcDEF-12_3")))).Link;
        Assert.Equal("https://www.youtube.com/watch?v=abcDEF-12_3", link.AbsoluteUri);
        Assert.Equal("https://www.youtube.com/watch?v=abc", TrailerLaunch.WatchLink(" abc ")!.AbsoluteUri);

        // The key is only ever the query value: it can't move the link to another host, path or fragment.
        var odd = TrailerLaunch.WatchLink("x&list=y/../@evil.example#z")!;
        Assert.True(TrailerLaunch.IsAllowedLink(odd));
        Assert.Equal("www.youtube.com", odd.Host);
        Assert.Equal("/watch", odd.AbsolutePath);
        Assert.Empty(odd.Fragment);
        Assert.Equal("?v=x&list=y/../@evil.example#z", Uri.UnescapeDataString(odd.Query));

        Assert.Null(TrailerLaunch.WatchLink("  "));
        Assert.Null(TrailerLaunch.WatchLink(null));
    }

    [Fact]
    public void OtherSitesAndBlankKeysStartNothing()
    {
        // TrailerRail.kt: YouTube is the only remote site the clients can open.
        Assert.Null(TrailerLaunch.For(Card(new RemoteVideoSource("vimeo", "123"))));
        Assert.Null(TrailerLaunch.For(Card(new RemoteVideoSource("youtube", " "))));
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=a", true)]
    [InlineData("https://youtube.com/watch?v=a", true)]
    [InlineData("https://youtu.be/a", true)]
    [InlineData("https://WWW.YouTube.COM/watch?v=a", true)]
    [InlineData("http://www.youtube.com/watch?v=a", false)]
    [InlineData("https://m.youtube.com/watch?v=a", false)]
    [InlineData("https://www.youtube.com.evil.example/watch?v=a", false)]
    [InlineData("https://evil.example/?u=https://www.youtube.com/", false)]
    [InlineData("https://user@www.youtube.com/watch?v=a", false)]
    [InlineData("https://www.youtube.com:8443/watch?v=a", false)]
    [InlineData("vnd.youtube:a", false)]
    [InlineData("youtube://a", false)]
    [InlineData("file:///C:/a.mp4", false)]
    public void OnlyHttpsLinksOnYouTubeMayBeHandedOn(string link, bool allowed) =>
        Assert.Equal(allowed, TrailerLaunch.IsAllowedLink(new Uri(link)));

    [Fact]
    public void AMissingOrRelativeLinkIsNeverHandedOn()
    {
        Assert.False(TrailerLaunch.IsAllowedLink(null));
        Assert.False(TrailerLaunch.IsAllowedLink(new Uri("/watch?v=a", UriKind.Relative)));
    }

    [Fact]
    public void AnExtraStartsOnItsOwnFileWhileItsDetailListsIt()
    {
        var extra = new ExtraPlayback("x", "2");
        Assert.Equal(new PlaybackSelection("2"), TrailerLaunch.Selection(Extra("1", "2"), extra));
        // resolveTvPlaybackStartSelection: a file the detail no longer lists falls back to the usual choice.
        Assert.Equal(PlaybackSelection.Auto, TrailerLaunch.Selection(Extra("1"), extra));
        Assert.Equal(PlaybackSelection.Auto, TrailerLaunch.Selection(Extra("1", "2"), extra with { FileId = null }));
        // Bale's player never takes a series or season container.
        Assert.Null(TrailerLaunch.Selection(Extra("1") with { Type = "series" }, extra));
        Assert.Null(TrailerLaunch.Selection(Extra("1") with { Type = "season" }, extra));
    }

    [Fact]
    public void AnExtraPlaysFromItsStartAndSavesNoTrackPreference()
    {
        // isExplicitStartOver: neither the extra's saved position nor the resume rewind applies.
        Assert.True(PlaybackStartPosition.IsExplicitStartOver(TrailerLaunch.StartPosition));
        Assert.Equal(0.0, PlaybackStartPosition.ForStartRequest(TrailerLaunch.StartPosition, detailPosition: 600));
        // A series' extra names its series (silo-server buildExtraItemDetail), but a pick in its player is saved for
        // neither the series nor the extra.
        Assert.Null(PlayerTrackPreferences.OwnerId(Extra("1") with { SeriesId = "series-tvdb-1", SeriesTitle = "Example Show" }));
        Assert.Null(PlayerTrackPreferences.OwnerId(Extra("1")));
    }

    [Fact]
    public void ThePlayerEyebrowNamesASeriesExtrasSeriesAndAFilmExtrasYear()
    {
        // tvPlayerEyebrow: a series title wins (no episode code on an extra); a film's extra is no "MOVIE".
        Assert.Equal("EXAMPLE SHOW", PlaybackControlsText.Eyebrow(Extra("1") with { SeriesId = "s", SeriesTitle = "Example Show", Year = 2022 }));
        Assert.Equal("2019", PlaybackControlsText.Eyebrow(Extra("1") with { Year = 2019 }));
        Assert.Null(PlaybackControlsText.Eyebrow(Extra("1")));
        Assert.Equal("Behind the Scenes", PlaybackControlsText.Title(Extra("1")));
    }

    [Fact]
    public void TheExtrasOwnDetailCarriesItsTypeSeriesAndFiles()
    {
        using var client = new SiloClient(new Uri("https://silo.example.com"), new ClientIdentity("d", "Test", "xbox", "Bale"), new StubHandler());
        var detail = client.Detail.ToDetail(new CatalogItemDetail
        {
            ContentId = "local-1", Type = "extra", Title = "Making Of", SeriesId = "series-tvdb-1", SeriesTitle = " Example Show ",
            Versions = [new FileVersion { FileId = "5" }],
        });
        Assert.Equal("extra", detail.Type);
        Assert.Equal("Making Of", detail.Title);
        Assert.Equal("Example Show", detail.SeriesTitle);
        Assert.Equal(new PlaybackSelection("5"), TrailerLaunch.Selection(detail, new ExtraPlayback("local-1", "5")));
        // A movie carries no series title.
        Assert.Null(client.Detail.ToDetail(new CatalogItemDetail { ContentId = "m", Type = "movie", SeriesTitle = " " }).SeriesTitle);
    }

    private static TrailerCard Card(TrailerSource source) => new("key", "Trailer", null, null, source);

    private static ItemDetail Extra(params string[] fileIds) =>
        new("x", "extra", "Behind the Scenes", null, null, null, null, null, [], null, [], [], null, [], null, "x", null, null,
            new(false, false, false), [])
        {
            Versions = fileIds.Select(id => new FileVersion { FileId = id }).ToList(),
        };
}
