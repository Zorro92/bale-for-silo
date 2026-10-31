using System.Net;
using System.Text.Json;
using Bale.Core.Browse;
using Bale.Core.Connection;
using Bale.Core.Detail;
using Bale.Core.People;
using Microsoft.Kiota.Abstractions;

namespace Bale.Core.Tests;

public class CardMenuTests
{
    private static readonly DateTimeOffset Stamp = DateTimeOffset.Parse("2026-10-01T12:00:00Z");

    private static MediaCard Card(string id = "movie", string type = "movie", string? series = null) =>
        new(id, type, "Title", null, null, null, null, null, null, null, null, null, series, [], null, null, null);

    [Fact]
    public void CatalogMenuMatchesOfficialOrderLabelsAndIcons()
    {
        Assert.Equal(
        [
            new(CardAction.Watched, "Mark as Watched", CardMenuIcon.Check),
            new(CardAction.Favorite, "Add to Favorites", CardMenuIcon.FavoriteBorder),
            new(CardAction.Watchlist, "Add to Watchlist", CardMenuIcon.BookmarkAdd),
        ], CardMenu.ForCard(Card()));
        Assert.Equal(
        [
            new(CardAction.Watched, "Mark as Unwatched", CardMenuIcon.VisibilityOff),
            new(CardAction.Favorite, "Remove from Favorites", CardMenuIcon.Favorite),
            new(CardAction.Watchlist, "Remove from Watchlist", CardMenuIcon.BookmarkRemove),
        ], CardMenu.ForCard(Card() with { Flags = new(true, true, true) }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DetailEpisodeHasOnlyWatchedAndFavorite(bool on)
    {
        var entries = CardMenu.ForEpisode(on, on);
        Assert.Equal([CardAction.Watched, CardAction.Favorite], entries.Select(entry => entry.Action));
        Assert.Equal(on ? "Mark as Unwatched" : "Mark as Watched", entries[0].Label);
        Assert.Equal(on ? CardMenuIcon.VisibilityOff : CardMenuIcon.Check, entries[0].Icon);
        Assert.Equal(on ? CardMenuIcon.Favorite : CardMenuIcon.FavoriteBorder, entries[1].Icon);
    }

    [Theory]
    [InlineData(null, "Play")]
    [InlineData(0d, "Resume")]
    [InlineData(60d, "Resume")]
    public void OnlyContinueWatchingPrependsPlayOrResume(double? position, string label)
    {
        var card = Card("episode", "episode", "show") with { PositionSeconds = position, ProgressUpdatedAt = Stamp };
        var menu = CardMenu.ForCard(card, home: true, sectionType: "continue_watching");
        Assert.Equal([CardAction.Play, CardAction.Watched, CardAction.Favorite, CardAction.Watchlist, CardAction.RemoveFromHome],
            menu.Select(entry => entry.Action));
        Assert.Equal(new(CardAction.Play, label, CardMenuIcon.Play), menu[0]);
        Assert.Equal(new(CardAction.RemoveFromHome, "Remove from Continue Watching", CardMenuIcon.Close), menu[^1]);
        Assert.DoesNotContain(CardMenu.ForCard(card, home: true, sectionType: "next_up"), entry => entry.Action == CardAction.Play);
        Assert.DoesNotContain(CardMenu.ForCard(card, sectionType: "continue_watching"), entry => entry.Action == CardAction.Play);
    }

    [Fact]
    public void RemovalIsHomeProgressOnlyAndNeedsAnAnchor()
    {
        var next = Card("episode", "episode", "show");
        var entries = CardMenu.ForCard(next, home: true, sectionType: "next_up");
        Assert.Equal([CardAction.Watched, CardAction.Favorite, CardAction.Watchlist, CardAction.RemoveFromHome], entries.Select(entry => entry.Action));
        Assert.Equal("Remove from Next Up", entries[^1].Label);
        Assert.DoesNotContain(CardMenu.ForCard(next, home: true, sectionType: "recently_added"), entry => entry.Action == CardAction.RemoveFromHome);
        Assert.DoesNotContain(CardMenu.ForCard(next, sectionType: "next_up"), entry => entry.Action == CardAction.RemoveFromHome);
        Assert.DoesNotContain(CardMenu.ForCard(Card(), home: true, sectionType: "next_up"), entry => entry.Action == CardAction.RemoveFromHome);
        Assert.Null(HomeCardDismissal.For(Card() with { SeriesId = " " }));
        Assert.Equal("continue_watching", HomeCardDismissal.For(next with { ProgressUpdatedAt = Stamp })!.Surface);
    }

    [Theory]
    [InlineData("continue_watching", true, true)]
    [InlineData("IN_PROGRESS", true, true)]
    [InlineData("next_up", false, true)]
    [InlineData("custom_up_next", false, true)]
    [InlineData("recently_added", false, false)]
    public void SectionDetectionMatchesSkylineFeed(string section, bool play, bool progress)
    {
        Assert.Equal(play, CardMenu.IsContinueWatching(section));
        Assert.Equal(progress, CardMenu.IsProgressRow(section));
    }

    [Theory]
    [InlineData("episode")]
    [InlineData("episodes")]
    [InlineData("series")]
    public void ShowDismissalRemovesItsWholeShowOnlyFromProgressRows(string type)
    {
        var dismissed = Card(type == "series" ? "show" : "e1", type, "show") with { ProgressUpdatedAt = Stamp };
        var otherEpisode = Card("e2", "episode", "show");
        var unrelated = Card("other");
        var rows = new[]
        {
            new MediaRow("cw", "Continue Watching", "continue_watching", false, CardShape.Landscape, [dismissed, unrelated]),
            new MediaRow("nu", "Next Up", "next_up", false, CardShape.Landscape, [otherEpisode]),
            new MediaRow("catalog", "Shows", "recently_added", false, CardShape.Poster, [dismissed, otherEpisode]),
        };
        var result = HomeCardDismissal.Remove(rows, dismissed);
        Assert.Equal(["cw", "catalog"], result.Select(row => row.Id));
        Assert.Equal([unrelated], result[0].Cards);
        Assert.Same(rows[2], result[1]);
    }

    [Fact]
    public void MovieAndAudiobookDismissalsRespectTheObservedStampNotBookSeries()
    {
        var book = Card("book", "audiobook", "book-series") with { ProgressUpdatedAt = Stamp };
        var resumed = book with { ProgressUpdatedAt = Stamp.AddMinutes(1) };
        var sibling = Card("other-book", "audiobook", "book-series");
        var row = new MediaRow("cw", "Continue Watching", "continue_watching", false, CardShape.Landscape, [book, resumed, sibling]);
        Assert.Equal([resumed, sibling], HomeCardDismissal.Remove([row], book)[0].Cards);
    }

    [Fact]
    public void ARefreshedProgressAnchorCannotDropTheShowFromNewerRows()
    {
        var dismissed = Card("e", "episode", "show") with { ProgressUpdatedAt = Stamp };
        var resumed = dismissed with { ProgressUpdatedAt = Stamp.AddMinutes(1) };
        var rows = new[]
        {
            new MediaRow("cw", "Continue Watching", "continue_watching", false, CardShape.Landscape, [resumed]),
            new MediaRow("nu", "Next Up", "next_up", false, CardShape.Landscape, [Card("e2", "episode", "show")]),
        };
        Assert.Same(rows, HomeCardDismissal.Remove(rows, dismissed));
        Assert.Same(rows, HomeCardDismissal.Remove(rows, Card()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EitherWatchedDirectionClearsCardAndEpisodeResume(bool played)
    {
        var card = (Card() with { PositionSeconds = 80, Progress = 0.5, RemainingMinutes = 20, Flags = new(true, true, !played) }).WithPlayed(played);
        Assert.Equal(new(true, true, played), card.Flags);
        Assert.Equal(0, card.PositionSeconds);
        Assert.Null(card.Progress);
        Assert.Null(card.RemainingMinutes);
        var episode = new EpisodeCard("e", 1, 2, "Title", null, null, null, 40, 0.5, !played)
        {
            ResumeSeconds = 80,
            IsInProgress = true,
        }.WithPlayed(played);
        Assert.Equal(played, episode.Played);
        Assert.Null(episode.Progress);
        Assert.Null(episode.ResumeSeconds);
        Assert.False(episode.IsInProgress);
    }

    [Theory]
    [InlineData(CardAction.Watched, false, "POST", "watched")]
    [InlineData(CardAction.Watched, true, "DELETE", "watched")]
    [InlineData(CardAction.Favorite, false, "PUT", "favorites")]
    [InlineData(CardAction.Favorite, true, "DELETE", "favorites")]
    [InlineData(CardAction.Watchlist, false, "PUT", "watchlist")]
    [InlineData(CardAction.Watchlist, true, "DELETE", "watchlist")]
    public async Task TogglesReuseDetailEndpoints(CardAction action, bool before, string method, string endpoint)
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.NoContent, "");
        var (browse, detail) = Feeds(handler);
        var flags = new ItemFlags(before, before, before);
        var result = await CardMenu.ToggleAsync(detail, "item", flags, action);
        Assert.Equal(CardMenu.Toggle(flags, action), result);
        Assert.Equal(method, handler.Requests.Single().Method.Method);
        Assert.Equal($"/api/v2/{endpoint}/item", handler.Requests.Single().RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task FailedWritePropagatesWithoutChangingTheSnapshot()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.InternalServerError, """{"title":"Write failed","status":500}""");
        var (_, detail) = Feeds(handler);
        var flags = new ItemFlags(false, false, false);
        await Assert.ThrowsAnyAsync<ApiException>(() => CardMenu.ToggleAsync(detail, "item", flags, CardAction.Watched));
        Assert.False(flags.Played);
        Assert.Throws<ArgumentOutOfRangeException>(() => CardMenu.Toggle(flags, CardAction.Play));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DismissalSendsOnlyTheCorrectObservedAnchor(bool progress)
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.NoContent, "");
        var (browse, _) = Feeds(handler);
        var card = Card("episode", "episode", "show") with { ProgressUpdatedAt = progress ? Stamp : null };
        await browse.DismissFromHomeAsync(card);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal($"/api/v2/home/dismissals/{(progress ? "continue_watching" : "next_up")}/episode", request.RequestUri!.AbsolutePath);
        using var body = JsonDocument.Parse(handler.RequestBodies.Single()!);
        if (progress)
        {
            Assert.Equal(Stamp, body.RootElement.GetProperty("progress_updated_at").GetDateTimeOffset());
            Assert.False(body.RootElement.TryGetProperty("series_id", out _));
        }
        else
        {
            Assert.Equal("show", body.RootElement.GetProperty("series_id").GetString());
            Assert.False(body.RootElement.TryGetProperty("progress_updated_at", out _));
        }
    }

    [Fact]
    public async Task InvalidDismissalNeverSendsARequestAndFailedDismissalPropagates()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.Conflict, """{"title":"Progress changed","status":409}""");
        var (browse, _) = Feeds(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => browse.DismissFromHomeAsync(Card()));
        await Assert.ThrowsAsync<ArgumentException>(() => browse.DismissFromHomeAsync(Card("") with { ProgressUpdatedAt = Stamp }));
        Assert.Empty(handler.Requests);
        await Assert.ThrowsAnyAsync<ApiException>(() => browse.DismissFromHomeAsync(Card() with { ProgressUpdatedAt = Stamp }));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task EpisodeFavoriteReadsMembershipAndTreatsOnly404AsAbsent()
    {
        var handler = new StubHandler()
            .Enqueue("""{"item_id":"e"}""")
            .Enqueue(HttpStatusCode.NotFound, """{"status":404}""")
            .Enqueue(HttpStatusCode.InternalServerError, """{"status":500}""");
        var (_, detail) = Feeds(handler);
        Assert.True(await detail.IsFavoriteAsync("e"));
        Assert.False(await detail.IsFavoriteAsync("e"));
        await Assert.ThrowsAnyAsync<ApiException>(() => detail.IsFavoriteAsync("e"));
    }

    [Fact]
    public void CatalogConversionPreservesMenuFlagsAndDismissalAnchors()
    {
        var (browse, _) = Feeds(new StubHandler());
        var card = browse.ToCard(new Api.Models.CatalogItem
        {
            ContentId = "e",
            Type = "episode",
            SeriesId = "show",
            UserState = new Api.Models.CatalogItemUserState { Played = true, IsFavorite = true, InWatchlist = true },
            PositionSeconds = 80,
            ProgressUpdatedAt = Stamp,
        }, CardShape.Landscape);
        Assert.Equal(new(true, true, true), card.Flags);
        Assert.Equal(80, card.PositionSeconds);
        Assert.Equal(Stamp, card.ProgressUpdatedAt);
        Assert.Equal("continue_watching", HomeCardDismissal.For(card)!.Surface);
    }

    [Fact]
    public void SearchAndPersonSnapshotsKeepOrderAndPagingAfterCardActions()
    {
        var first = Card("first");
        var neighbor = Card("neighbor");
        var continuation = new SearchContinuation("next", ["next"]);
        var page = new SearchResultPage([first, neighbor], 100, false, continuation);
        var search = new SearchResults();
        var request = search.BeginSearch("title", SearchMediaFilter.Movies)!;
        Assert.True(search.ApplyPage(request, page));
        var works = new PersonWorks();
        Assert.True(works.Apply(works.Begin(PersonMediaFilter.All), page));
        var updated = first with { Flags = new(true, true, true) };
        search.UpdateCard(updated);
        works.UpdateCard(updated);
        search.UpdateCard(Card("absent"));
        works.UpdateCard(Card("absent"));
        Assert.Equal([updated, neighbor], search.Items);
        Assert.Equal([updated, neighbor], works.Items);
        Assert.Same(neighbor, search.Items[1]);
        Assert.Same(neighbor, works.Items[1]);
        Assert.Same(continuation, search.Continuation);
        Assert.Same(continuation, works.Continuation);
        Assert.Equal(request.Generation, search.Generation);
        Assert.Equal(100, search.Total);
        Assert.Equal(100, works.Total);
    }

    private static (BrowseFeed Browse, DetailFeed Detail) Feeds(StubHandler handler)
    {
        var server = new Uri("https://silo.example.com");
        var connection = new SiloConnection(server, new ClientIdentity("d", "x", "xbox", "Bale"), transport: handler);
        var browse = new BrowseFeed(connection.Api, server);
        return (browse, new DetailFeed(connection.Api, browse));
    }
}
