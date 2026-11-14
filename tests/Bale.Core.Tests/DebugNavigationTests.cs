using System.Globalization;
using Bale.Core.Api.Models;
using Bale.Core.Detail;
using Bale.Core.Playback;
using Bale.Xbox.Services;

namespace Bale.Core.Tests;

public class DebugNavigationTests
{
    [Theory]
    [InlineData("movie-imdb-tt0000000", false)]
    [InlineData("episode-imdb-tt0000000-s01e02", true)]
    public void ParsesContentAndExactFileAndInvariantSourceTime(string id, bool play)
    {
        var query = new Dictionary<string, string> { ["id"] = id, ["file"] = "file-1" };
        if (play) query["start"] = "123.5";
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.True(DebugNavigationRequest.TryParse(query, play, out var request));
            Assert.Equal(new(id, "file-1", play ? 123.5 : null), request);
            if (play) Assert.Equal(123.5, PlaybackStartPosition.ForStartRequest(request.StartSeconds, 999, rewindSeconds: 0));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("id", "")]
    [InlineData("id", "movie/id")]
    [InlineData("id", "movie\nnext")]
    [InlineData("id", "https://example.org/?token=private")]
    [InlineData("file", "")]
    [InlineData("file", "file?token=private")]
    [InlineData("start", "NaN")]
    [InlineData("start", "Infinity")]
    [InlineData("start", "-1")]
    [InlineData("start", "1e3")]
    [InlineData("start", "1,5")]
    [InlineData("start", " 1")]
    [InlineData("start", "")]
    [InlineData("start", "99999999999999999999999999999999999999")]
    [InlineData("unknown", "anything")]
    public void RejectsAmbiguousOrUnsafeInputBeforeNavigation(string key, string value)
    {
        var query = new Dictionary<string, string> { ["id"] = "movie-1", [key] = value };
        Assert.False(DebugNavigationRequest.TryParse(query, true, out _));
    }

    [Fact]
    public void OpenRejectsStartAndMissingIdAndPlayAllowsStartOver()
    {
        Assert.False(DebugNavigationRequest.TryParse(new Dictionary<string, string>(), true, out _));
        var query = new Dictionary<string, string> { ["id"] = "movie-1", ["start"] = "0" };
        Assert.False(DebugNavigationRequest.TryParse(query, false, out _));
        Assert.True(DebugNavigationRequest.TryParse(query, true, out var request));
        Assert.Equal(0, request.StartSeconds);
        query.Remove("start");
        Assert.True(DebugNavigationRequest.TryParse(query, false, out request));
        Assert.Null(request.StartSeconds);
        Assert.Null(request.FileId);
    }

    [Fact]
    public void VersionPickUsesThePickerStateAndNeverFallsBackForAnUnknownFile()
    {
        ItemDetail detail = new("movie-1", "movie", "", null, null, null, null, null,
            [], null, [], [], null, [], null, "movie-1", null, null, new(false, false, false), [])
        {
            Versions = [new FileVersion { FileId = "file-1", AudioTracks = [new(), new()] }, new FileVersion { FileId = "file-2" }],
            LastFileId = "file-1",
        };
        var selections = new PlaybackSelectionState();
        selections.Select(detail, PlaybackSelectorKind.Audio, new("", "", false, TrackIndex: 1));
        Assert.Equal(1, selections.For(detail).AudioIndex);
        Assert.True(new DebugNavigationRequest("movie-1", "file-2", null).SelectFile(detail, selections));
        Assert.Equal(new PlaybackSelection("file-2"), selections.For(detail));
        Assert.False(new DebugNavigationRequest("movie-1", "absent-file", null).SelectFile(detail, selections));
        Assert.Equal("file-2", selections.For(detail).FileId);
    }

    [Fact]
    public void RedirectedEpisodeMustMatchTheActualPlayTarget()
    {
        var request = new DebugNavigationRequest("episode-1", null, null);
        Assert.True(request.MatchesTarget("episode-1", "episode-1"));
        Assert.True(request.MatchesTarget("series-1", "episode-1"));
        Assert.False(request.MatchesTarget("series-1", "episode-2"));
        Assert.False(request.MatchesTarget("series-1", null));
    }

    [Theory]
    [InlineData(null, true, 50)]
    [InlineData("1", true, 1)]
    [InlineData("200", true, 200)]
    [InlineData("0", false, 0)]
    [InlineData("201", false, 201)]
    [InlineData("-1", false, 0)]
    [InlineData("", false, 0)]
    [InlineData(" 2", false, 0)]
    public void BoundsHistoryReads(string? text, bool valid, int count)
    {
        Assert.Equal(valid, DebugNavigationRequest.TryCount(text, out var parsed));
        if (valid) Assert.Equal(count, parsed);
    }

    [Fact]
    public void HistoryEvictsOldestEntriesKeepsOrderAndHasTimestamps()
    {
        var history = new DebugNavigationHistory();
        Assert.Empty(history.Last(50));
        for (var i = 0; i < 240; i++) history.Add("page", "DetailPage content=movie-" + i);
        var lines = history.Last(200).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(200, lines.Length);
        Assert.EndsWith("content=movie-40", lines[0]);
        Assert.EndsWith("content=movie-239", lines[^1]);
        Assert.True(DateTimeOffset.TryParse(lines[0].Split(' ')[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
        Assert.Equal(lines.TakeLast(2), history.Last(2).Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void HistoryIsThreadSafeAndRefusesUnsafeRouteIdentities()
    {
        var history = new DebugNavigationHistory();
        Parallel.For(0, 500, i => history.Add("command", "key enter HTTP=200 focus=Button#PlayButton"));
        Assert.Equal(200, history.Last(200).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        foreach (var value in new[] { "https://example.org/?token=private", "a\nb", "token=private", "a/b", new string('x', 257) })
            Assert.Equal("redacted", DebugNavigationHistory.Identity(value));
    }

    [Fact]
    public void NavigationUsesNormalPlayAndShutdownAndCannotPickAProfileOrInjectInput()
    {
        var server = Read("DebugServer.Navigation.cs.txt");
        var page = Read("DetailPage.DebugNavigation.cs.txt");
        var player = Read("PlayerPage.DebugNavigation.cs.txt");
        foreach (var source in new[] { server, page, player }) Assert.StartsWith("#if DEBUG_SERVER", source);
        Assert.Contains("no profile selected", server);
        Assert.Contains("SameProfile()", server);
        Assert.Contains("frame.BackStack.Clear()", server);
        Assert.Contains("frame.ForwardStack.Clear()", server);
        Assert.Contains("shell?.DebugHomeReady == true", server);
        Assert.Contains("_debugNavigationLoaded && !_searchSelected", Read("ShellPage.DebugNavigation.cs.txt"));
        Assert.Contains("_debugNavigationLoaded = false", Read("ShellPage.cs.txt"));
        Assert.Contains("!PageFrame.IsTransitioning", server);
        Assert.Contains("DebugStopForNavigationAsync().WaitAsync(cancellation)", server);
        Assert.Contains("DebugStopForNavigationAsync() => ShutdownAsync()", player);
        Assert.Contains("detail.DebugOpenPlayerAsync(request)", server);
        Assert.Contains("OpenPlayerAsync(target, request.StartSeconds, fromEpisode: false)", page);
        Assert.Contains("SuppressResumeRewind = true", Read("DetailPage.Player.cs.txt"));
        Assert.Contains("DebugServer.RecordNavigation(e)", Read("PageFrame.cs.txt"));
        Assert.Contains("DebugNavigationHistory.PlayerEvent(message)", Read("Log.cs.txt"));
        Assert.DoesNotContain("ex.Message", server + page + player);
        foreach (var forbidden in new[] { "UseProfile(", "ContinueToHome(", "InjectKeyboard", "Playback.StartAsync(" })
            Assert.DoesNotContain(forbidden, server + page);
        var routes = Read("DebugServer.cs.txt");
        foreach (var path in new[] { "/open", "/play", "/home", "/navlog" }) Assert.Contains($"\"{path}\"", routes);
        Assert.Contains("if (duplicateQueryKeys", routes);
    }

    private static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, name));
}
