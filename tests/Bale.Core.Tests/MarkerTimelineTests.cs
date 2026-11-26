using System.Text.Json;
using Bale.Core.Api.Models;
using Bale.Core.Playback;
using Microsoft.Kiota.Abstractions.Serialization;
using Microsoft.Kiota.Serialization.Json;

namespace Bale.Core.Tests;

/// <summary>silo-android MarkerTimelineTest.kt, plus the two shapes Bale reads markers from.</summary>
public class MarkerTimelineTests
{
    // A mid-credits scene plays between two credits ranges (the Android test's TheIntroDB example).
    private static readonly MarkerTimeline Timeline = MarkerTimeline.Resolve(
    [
        (MarkerKind.Recap, 20, 107), (MarkerKind.Intro, 108, 220),
        (MarkerKind.Credits, 2975.5, 3167.4), (MarkerKind.Credits, 3189, 3300),
    ], intro: new(108, 220), credits: new(2975.5, 3167.4), recap: new(20, 107));

    [Fact]
    public void EachCreditsRangeIsActiveOnItsOwnAndTheGapIsNot()
    {
        Assert.Equal(new MarkerRange(2975.5, 3167.4), Timeline.Active(MarkerKind.Credits, 3000));
        Assert.Null(Timeline.Active(MarkerKind.Credits, 3167.4));
        Assert.Null(Timeline.Active(MarkerKind.Credits, 3180));
        Assert.Equal(new MarkerRange(3189, 3300), Timeline.Active(MarkerKind.Credits, 3189));
        Assert.Equal(new MarkerRange(3189, 3300), Timeline.Active(MarkerKind.Credits, 3250));
        Assert.Null(Timeline.Active(MarkerKind.Credits, 2975.4));
        Assert.Equal(new MarkerRange(20, 107), Timeline.Active(MarkerKind.Recap, 50));
        Assert.Null(Timeline.Active(MarkerKind.Recap, 3000));
        Assert.Null(Timeline.Active(MarkerKind.Credits, double.NaN));
    }

    [Fact]
    public void SingularFieldsStandInWhenTheServerSendsNoSegments()
    {
        var legacy = MarkerTimeline.Resolve(null, intro: new(0, 30), credits: new(1200, 1260), preview: new(1260, 1290));
        Assert.Equal(new MarkerRange(0, 30), legacy.Active(MarkerKind.Intro, 10));
        Assert.Equal(new MarkerRange(1200, 1260), legacy.Active(MarkerKind.Credits, 1210));
        Assert.Null(legacy.Active(MarkerKind.Recap, 10));
    }

    [Fact]
    public void AnEmptySegmentListMeansNoMarkersEvenWithStaleSingularFields()
    {
        var cleared = MarkerTimeline.Resolve([], intro: new(0, 30));
        Assert.Null(cleared.Active(MarkerKind.Intro, 10));
        Assert.True(cleared.IsEmpty);
    }

    [Fact]
    public void UnsortedAndInvalidSegmentsResolveToOrderedValidRanges()
    {
        var messy = MarkerTimeline.Resolve(
        [
            (MarkerKind.Credits, 3189, 3300), (MarkerKind.Credits, 50, 40), (MarkerKind.Credits, -5, 10),
            (MarkerKind.Credits, 2975.5, 3167.4), (MarkerKind.Credits, 100, double.PositiveInfinity),
        ]);
        Assert.Equal([new MarkerRange(2975.5, 3167.4), new MarkerRange(3189, 3300)], messy.Ranges(MarkerKind.Credits));
        Assert.Null(messy.Active(MarkerKind.Credits, 5));
        Assert.Equal(new MarkerRange(3189, 3300), messy.Active(MarkerKind.Credits, 3200));
    }

    [Fact]
    public void IntroPromptFollowsTheOccurrenceThatLastStarted()
    {
        var twoIntros = MarkerTimeline.Resolve([(MarkerKind.Intro, 600, 660), (MarkerKind.Intro, 30, 90)]);
        // Before any starts, the first: an undo stays up when the viewer scrubs back past the start of the intro it skipped.
        Assert.Equal(new MarkerRange(30, 90), twoIntros.OccurrenceAt(MarkerKind.Intro, 10));
        Assert.Null(MarkerTimeline.Empty.OccurrenceAt(MarkerKind.Intro, 10));
        Assert.Equal(new MarkerRange(30, 90), twoIntros.OccurrenceAt(MarkerKind.Intro, 40));
        // After an automatic skip lands on the end, the undo stays anchored to it.
        Assert.Equal(new MarkerRange(30, 90), twoIntros.OccurrenceAt(MarkerKind.Intro, 90));
        Assert.Equal(new MarkerRange(30, 90), twoIntros.OccurrenceAt(MarkerKind.Intro, 300));
        Assert.Equal(new MarkerRange(600, 660), twoIntros.OccurrenceAt(MarkerKind.Intro, 600));
    }

    [Fact]
    public void FinalCreditsStartSkipsAMidCreditsScene()
    {
        Assert.Equal(3189, Timeline.FinalCreditsStart());
        var touching = MarkerTimeline.Resolve([(MarkerKind.Credits, 1200, 1250), (MarkerKind.Credits, 1250.5, 1300)]);
        Assert.Equal(1200, touching.FinalCreditsStart());
        Assert.Equal(1200, MarkerTimeline.Resolve(null, credits: new(1200, 1260)).FinalCreditsStart());
        Assert.Null(MarkerTimeline.Empty.FinalCreditsStart());
    }

    [Fact]
    public void ACatalogVersionGivesItsSingularMarkers()
    {
        var version = Version("""
            {"file_id":"7","intro":{"start":12.5,"end":80},"credits":{"start":1290,"end":1380},"recap":{"start":0,"end":12}}
            """);
        var markers = MarkerTimeline.From(version);
        Assert.Equal(new MarkerRange(12.5, 80), markers.Active(MarkerKind.Intro, 20));
        Assert.Equal(new MarkerRange(1290, 1380), markers.Active(MarkerKind.Credits, 1300));
        Assert.Equal(new MarkerRange(0, 12), markers.Active(MarkerKind.Recap, 5));
        Assert.True(MarkerTimeline.From((FileVersion?)null).IsEmpty);
        Assert.True(MarkerTimeline.From(Version("""{"file_id":"7"}""")).IsEmpty);
    }

    [Fact]
    public void TheFileMarkersReadGivesEveryOccurrence()
    {
        // getFileMarkers: marker_segments lists both credits ranges; the singular credits projects only the first.
        var markers = MarkerTimeline.From(Markers("""
            {"file_id":"7","intro":{"start_seconds":30,"end_seconds":90,"source":"online"},
             "credits":{"start_seconds":2975.5,"end_seconds":3167.4},"recap":{},"preview":{},
             "marker_segments":[{"kind":"intro","start_seconds":30,"end_seconds":90},
               {"kind":"credits","start_seconds":2975.5,"end_seconds":3167.4,"thumbnail_url":"/t.jpg"},
               {"kind":"credits","start_seconds":3189,"end_seconds":3300},{"kind":"chapter","start_seconds":0,"end_seconds":5}]}
            """));
        Assert.Equal(2, markers.Count(MarkerKind.Credits));
        Assert.Equal(new MarkerRange(3189, 3300), markers.Active(MarkerKind.Credits, 3200));
        Assert.Equal(new MarkerRange(30, 90), markers.Active(MarkerKind.Intro, 31));
        Assert.Equal(3189, markers.FinalCreditsStart());
        // A kind Bale does not know is left out, not read as another.
        Assert.Equal(1, markers.Count(MarkerKind.Intro));
        Assert.Equal(0, markers.Count(MarkerKind.Recap) + markers.Count(MarkerKind.Preview));
        // A server that leaves the list out: the singular fields stand in; an empty list clears them.
        var legacy = MarkerTimeline.From(Markers("""{"file_id":"7","intro":{"start_seconds":30,"end_seconds":90}}"""));
        Assert.Equal(new MarkerRange(30, 90), legacy.Active(MarkerKind.Intro, 31));
        Assert.True(MarkerTimeline.From(Markers(
            """{"file_id":"7","intro":{"start_seconds":30,"end_seconds":90},"marker_segments":[]}""")).IsEmpty);
    }

    private static FileVersion Version(string json) => Parse(json, FileVersion.CreateFromDiscriminatorValue);

    private static FileMarkers Markers(string json) => Parse(json, FileMarkers.CreateFromDiscriminatorValue);

    /// <summary>Through Kiota's own JSON parser, so the wire names are the generated client's.</summary>
    private static T Parse<T>(string json, ParsableFactory<T> factory) where T : IParsable =>
        new JsonParseNode(JsonDocument.Parse(json).RootElement).GetObjectValue(factory)!;
}

/// <summary>The playing file's markers read (getFileMarkers), as the player sends it.</summary>
public class FileMarkersReadTests
{
    [Fact]
    public async Task ReadsThePlayingFilesMarkersAsTheActiveProfile()
    {
        var stub = new StubHandler().Enqueue("""
            {"file_id":"42","intro":{},"credits":{},"recap":{},"preview":{},
             "marker_segments":[{"kind":"intro","start_seconds":12,"end_seconds":70}]}
            """);
        using var client = PlaybackTestSupport.Create(stub);
        var markers = await client.Playback.FileMarkersAsync("42");
        Assert.Equal(new MarkerRange(12, 70), markers.Active(MarkerKind.Intro, 20));
        var request = Assert.Single(stub.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/v2/markers/files/42", request.RequestUri!.AbsolutePath);
        Assert.Equal("profile", request.Headers.GetValues("X-Profile-Id").Single());
    }

    [Fact]
    public async Task AFailedReadThrowsSoThePlayerKeepsTheDetailMarkers()
    {
        var stub = new StubHandler().Enqueue(System.Net.HttpStatusCode.NotFound, """{"status":404,"title":"Not Found"}""");
        using var client = PlaybackTestSupport.Create(stub);
        await Assert.ThrowsAnyAsync<Exception>(() => client.Playback.FileMarkersAsync("42"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.Playback.FileMarkersAsync(" "));
    }
}
