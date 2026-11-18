using Bale.Core.Api.Models;
using Bale.Core.Browse;
using Bale.Core.Connection;
using Microsoft.Kiota.Abstractions;

namespace Bale.Core.Tests;

/// <summary>CardYearsTests.swift (Apple 9718f17b): the years line under a series card.</summary>
public class CardYearsTests
{
    private static string? Caption(string type, long? year, string? status = null, string? lastAirDate = null) =>
        CardYears.Caption(type, year, status, lastAirDate is null ? null : new Date(DateTime.Parse(lastAirDate)));

    [Fact]
    public void AnEndedSeriesShowsTheYearsItRan()
    {
        Assert.Equal("2019 – 2023", Caption("series", 2019, "ended", "2023-05-28"));
        Assert.Equal("2014 – 2015", Caption("series", 2014, "cancelled", "2015-03-01"));
    }

    [Fact]
    public void ARunningSeriesRunsToPresent()
    {
        Assert.Equal("2021 – Present", Caption("series", 2021, "returning", "2025-12-28"));
        Assert.Equal("2021 – Present", Caption("series", 2021, "returning"));
    }

    [Fact]
    public void ASeriesThatStartedAndEndedInOneYearShowsThatYear() =>
        Assert.Equal("2022", Caption("series", 2022, "ended", "2022-05-04"));

    [Fact]
    public void ASeriesWithoutAStatusOrAnEndShowsItsFirstYear()
    {
        Assert.Equal("2019", Caption("series", 2019, "ended"));
        Assert.Equal("2019", Caption("series", 2019, null, "2023-05-28"));
        Assert.Equal("2019", Caption("series", 2019, "ended", "2018-01-01"));
        Assert.Equal("2019", Caption("series", 2019, "in_production", "2023-05-28"));
        Assert.Equal("2019", Caption("series", 2019, "upcoming"));
        Assert.Null(Caption("series", null, "returning"));
    }

    [Fact]
    public void OnlyASeriesShowsARange()
    {
        Assert.Equal("1995", Caption("movie", 1995, "ended", "1999-01-01"));
        Assert.Equal("2021", Caption("season", 2021, "returning"));
    }

    private static BrowseFeed Feed(StubHandler stub)
    {
        ServerAddress.TryParse("silo.example.com", out var address);
        var connection = new SiloConnection(address, new ClientIdentity("d", "x", "xbox", "Bale"), transport: stub);
        return new BrowseFeed(connection.Api, address);
    }

    [Fact]
    public async Task HistoryCardsKeepTheSeriesYears()
    {
        var stub = new StubHandler().Enqueue("""
            {"items":[{"content_id":"series:years","type":"series","title":"Synthetic","year":2021,"show_status":"returning",
            "last_air_date":"2025-12-28"}],"page":{"has_more":false}}
            """);
        var page = await Feed(stub).HistoryPageAsync(ArtworkWidths.Tv1080, null, [], default);
        Assert.Equal("2021 – Present", Assert.Single(page.Cards).Subtitle);
    }

    [Fact]
    public void CatalogCardsCaptionTheYearsASeriesRan()
    {
        var feed = Feed(new StubHandler());
        var series = new CatalogItem
        {
            ContentId = "series:years", Type = "series", Title = "Synthetic", Year = 2019, ShowStatus = "ended",
            LastAirDate = new Date(2023, 5, 28),
        };
        Assert.Equal("2019 – 2023", feed.ToCard(series, CardShape.Poster).Subtitle);
        var movie = new CatalogItem { ContentId = "movie:year", Type = "movie", Title = "Film", Year = 1995 };
        Assert.Equal("1995", feed.ToCard(movie, CardShape.Poster).Subtitle);
    }
}
