using System.Globalization;
using Bale.Core.Api.Models;
using Bale.Core.Requests;

namespace Bale.Core.Tests;

public class RequestPresentationTests
{
    [Theory]
    [InlineData("pending", "completed", "failed", RequestDisplay.Pending, "Pending", 1, 1)]
    [InlineData("approved", "queued", "active", RequestDisplay.OnTheWay, "Queued", 2, 2)]
    [InlineData("processing", "completed", "active", RequestDisplay.OnTheWay, "Adding to library", 3, 3)]
    [InlineData("partially_available", "downloading", "active", RequestDisplay.OnTheWay, "Partly in library", 3, 3)]
    [InlineData("available", "downloading", "active", RequestDisplay.InLibrary, "In library", 4, null)]
    [InlineData("declined", "pending", "active", RequestDisplay.Declined, "Declined", 1, 1)]
    [InlineData("failed", "pending", "active", RequestDisplay.Failed, "Failed", 2, 2)]
    [InlineData("cancelled", "pending", "active", RequestDisplay.Unavailable, "Unavailable", 0, null)]
    [InlineData(null, "pending", "declined", RequestDisplay.Declined, "Declined", 1, 1)]
    [InlineData(null, "completed", "active", RequestDisplay.InLibrary, "In library", 4, null)]
    [InlineData("future_state", "downloading", "active", RequestDisplay.OnTheWay, "Downloading", 2, 2)]
    [InlineData(null, "future_status", "active", RequestDisplay.OnTheWay, "On the way", 2, 2)]
    public void MapsUserStateBeforeLegacyStatusAndOutcome(string? state, string status, string outcome,
        RequestDisplay display, string label, int completed, int? current)
    {
        var progress = RequestPresentation.Progress(state, status, outcome);
        Assert.Equal(display, progress.Display);
        Assert.Equal(label, progress.ShortLabel);
        Assert.Equal(completed, progress.CompletedSteps);
        Assert.Equal(current, progress.CurrentStep);
    }

    [Fact]
    public void ActiveMissingSeasonsOutrankLibraryAvailabilityAndUnknownStateFallsBack()
    {
        var request = new RequestMediaState { State = "processing", Status = "completed", Requestable = false };
        Assert.Equal(RequestDisplay.OnTheWay, RequestPresentation.Progress("available", request)!.Display);
        Assert.Null(RequestPresentation.Progress("available", request)!.LibraryItemToOpen("series"));
        request.State = "future_state";
        Assert.Equal("series", RequestPresentation.Progress("available", request)!.LibraryItemToOpen("series"));
        Assert.Null(RequestPresentation.Progress("missing", new RequestMediaState { Requestable = true }));
        Assert.Equal("Request limit reached", RequestPresentation.Progress("missing", new RequestMediaState { Reason = "quota_exceeded" })!.LongLabel);
    }

    [Theory]
    [InlineData(true, true, FeatureStatus_state.Available, true)]
    [InlineData(true, false, FeatureStatus_state.Available, false)]
    [InlineData(false, true, FeatureStatus_state.Available, false)]
    [InlineData(true, true, FeatureStatus_state.Disabled, false)]
    [InlineData(true, true, FeatureStatus_state.Not_configured, false)]
    [InlineData(true, true, FeatureStatus_state.Unsupported, false)]
    [InlineData(true, null, FeatureStatus_state.Available, false)]
    [InlineData(true, true, null, false)]
    public void CapabilityFailsClosed(bool enabled, bool? allowed, FeatureStatus_state? state, bool expected) =>
        Assert.Equal(expected, RequestPresentation.IsEnabled(new FeatureStatus { RequestsEnabled = enabled, Allowed = allowed, State = state }));

    [Fact]
    public void GroupsOwnRequestsAndUsesServerDiscoverOrderSkippingUnsupportedRows()
    {
        var mine = new[]
        {
            Record("landed", "available", "2026-11-01"), Record("failed", "failed", "2026-11-02"),
            Record("old", "pending", "2026-10-01"), Record("new", "pending", "2026-10-02"),
            Record("cancelled", "cancelled", "2026-11-02"), Record("ebook", "pending", "2026-11-02", "ebook"),
        };
        var discover = new DiscoverSectionCollection
        {
            RowOrder = [new() { Key = "second", Type = "tmdb_chart" }, new() { Key = "brands", Type = "studios" },
                new() { Key = "first", Type = "tmdb_list" }, new() { Key = "empty", Type = "tmdb_chart" }],
            Items = [Section("first", Result("movie", 1)), Section("second", Result("series", 2), Result("ebook", 3)),
                Section("brands", Result("movie", 4)), Section("empty", Result("ebook", 5))],
        };
        var rows = RequestPresentation.Rows(mine, discover);
        Assert.Equal(["mine", "discover:second", "discover:first"], rows.Select(row => row.Key));
        Assert.Equal(["request:new", "request:old", "request:failed", "request:landed"], rows[0].Items.Select(item => item.Key));
        Assert.Null(rows[0].Eyebrow);
        Assert.Equal("Discover", rows[1].Eyebrow);
        Assert.Null(rows[2].Eyebrow);
        Assert.Single(rows[1].Items);
    }

    [Fact]
    public void LegacyDiscoveryInterleavesAndDeduplicatesMoviesAndSeries()
    {
        var rows = RequestPresentation.Rows([], new DiscoverSectionCollection
        {
            Items = [Section("trending_movies", Result("movie", 1), Result("movie", 1), Result("movie", 3)),
                Section("trending_series", Result("series", 2), Result("series", 4)),
                Section("popular_movies", Result("movie", 9)), Section("upcoming_movies", Result("movie", 10))],
        });
        Assert.Equal(["Trending now", "Crowd favorites"], rows.Select(row => row.Title));
        Assert.Equal([1L, 2L, 4L, 3L], rows[0].Items.Select(item => item.TmdbId));
        Assert.Empty(RequestPresentation.Rows([], new DiscoverSectionCollection { Items = [] }));
    }

    [Theory]
    [InlineData(" MOVIE ", true)]
    [InlineData("series", true)]
    [InlineData("audiobooks", true)]
    [InlineData("audiobook", true)]
    [InlineData("ebook", false)]
    [InlineData("person", false)]
    public void FiltersLikeTvRequestPresentation(string type, bool expected) => Assert.Equal(expected, RequestPresentation.Supports(type));

    [Theory]
    [InlineData("quota_exceeded", "Request limit reached")]
    [InlineData("blocked", "You can't request media right now")]
    [InlineData("future_code", null)]
    [InlineData("one or more targets failed", "One or more targets failed")]
    [InlineData("  ", null)]
    public void FormatsReasonsWithoutLeakingServerTokens(string reason, string? expected) => Assert.Equal(expected, RequestPresentation.ReasonCopy(reason));

    [Fact]
    public void FormatsDatesQualityAndArtwork()
    {
        Assert.Equal("Nov 2", RequestPresentation.Day(DateTimeOffset.Parse("2026-11-02T03:00:00Z"), TimeZoneInfo.Utc, CultureInfo.GetCultureInfo("en-US")));
        var targets = new[] { new RequestTarget { Quality = "1080p", Status = "downloading" }, new RequestTarget { Quality = "4K", Status = "queued" } };
        Assert.Equal("1080p downloading · 4K queued", RequestPresentation.QualitySummary(targets));
        Assert.Equal("1080p", RequestPresentation.QualitySummary(targets.Take(1)));
        Assert.Equal("https://image.tmdb.org/t/p/w500/poster.jpg", RequestPresentation.Image("/poster.jpg")!.ToString());
        Assert.Equal("https://image.tmdb.org/t/p/w780/backdrop.jpg", RequestPresentation.Image("/backdrop.jpg", true)!.ToString());
        Assert.Equal("https://example.com/poster.jpg", RequestPresentation.Image("https://example.com/poster.jpg")!.ToString());
        Assert.Null(RequestPresentation.Image(" "));
        Assert.Null(RequestPresentation.Image("file:///private"));
    }

    [Fact]
    public void PrimaryActionAndFollowGatingMatchOfficialDetailState()
    {
        var detail = Detail();
        var state = new RequestDetailState(detail, null, true);
        Assert.Equal(RequestPrimaryAction.Request, state.PrimaryAction);
        Assert.False(state.CanFollow);
        Assert.Equal("Requesting…", (state with { IsSubmitting = true }).PrimaryTitle);
        Assert.Equal("Not confirmed yet", (state with { SubmissionUnconfirmed = true }).PrimaryTitle);
        detail.Request = new() { State = "pending", Status = "pending", Reason = "already_requested" };
        Assert.True(state.CanFollow);
        Assert.Equal("Requested · Pending", state.PrimaryTitle);
        detail.Request.RequestedByViewer = true;
        Assert.False(state.CanFollow);
        detail.Request.RequestedByViewer = false;
        Assert.False((state with { FollowSupported = false }).CanFollow);
        detail.Request.State = "available";
        detail.LibraryContentId = "library";
        Assert.Equal(RequestPrimaryAction.OpenInLibrary, state.PrimaryAction);
        Assert.False(state.CanFollow);
    }

    [Fact]
    public void OwnRecordPreventsDuplicateAndEndedRequestOffersRequestAgain()
    {
        var detail = Detail();
        var own = Record("own", "processing", "2026-11-02");
        own.Status = "completed";
        var state = new RequestDetailState(detail, own);
        Assert.Equal(RequestPrimaryAction.Status, state.PrimaryAction);
        Assert.Equal("Adding to your library", state.PrimaryTitle);
        own.State = "pending";
        Assert.True(state.CanCancel);
        Assert.False((state with { IsCancelling = true }).CanCancel);
        Assert.False((state with { CancelUnconfirmed = true }).CanCancel);
        own.State = own.Outcome = "declined";
        own.OutcomeReason = "quota_exceeded";
        Assert.Equal("Request Again", state.PrimaryTitle);
        Assert.Equal(RequestDisplay.Declined, state.Progress!.Display);
        Assert.Equal("Declined · Request limit reached", state.Progress.DetailTitle);
        detail.Request = new() { State = "available" };
        Assert.Null(state.EndedRequest);
    }

    internal static RequestMediaDetail Detail() => new()
    {
        MediaType = "movie", TmdbId = 1, Title = "A title", Request = new() { Requestable = true }, Availability = "missing",
    };
    private static MediaRequest Record(string id, string state, string date, string type = "movie") => new()
    {
        Id = id, MediaType = type, TmdbId = 1, State = state, Status = "pending", Outcome = "active",
        CreatedAt = DateTimeOffset.Parse(date, CultureInfo.InvariantCulture),
    };
    private static RequestMediaResult Result(string type, long id) => new() { MediaType = type, TmdbId = id, Title = "Title", Request = new() { Requestable = true } };
    private static DiscoverSection Section(string key, params RequestMediaResult[] items) => new() { Key = key, Title = key, Results = [.. items] };
}
