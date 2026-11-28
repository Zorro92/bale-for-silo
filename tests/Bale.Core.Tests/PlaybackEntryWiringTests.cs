namespace Bale.Core.Tests;

/// <summary>
/// The two playback entries that used to show a placeholder: an episode card's A press on Detail, and Continue Watching's
/// Play/Resume on Home. Both start playback through the path a Detail page uses (DetailPage.cs.txt), and Home's Play
/// pushes the player over the shell without a Detail page (BrowsePage.cs.txt, ShellPage.cs.txt). The Xbox pages compile
/// only on Windows, so these read their sources, as the other wiring tests do.
/// </summary>
public sealed class PlaybackEntryWiringTests
{
    [Fact]
    public void NoXboxSourceStillHasThePlaceholderNotice()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "xbox-src");
        var files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories);
        Assert.True(files.Length >= 200, $"only {files.Length} Xbox sources found");
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("ShowComingAsync", text);
            Assert.DoesNotContain("Playback is next", text);
            Assert.DoesNotContain("next milestone", text);
        }
    }

    [Fact]
    public void AnEpisodeCardsAPressAndAClickPlayTheSameEpisodePath()
    {
        var detail = Read("DetailPage.cs.txt");
        // Keyboard and gamepad A reach the episode through CardMenuHost; a pointer click still raises ItemClick.
        Assert.Contains("if (item is EpisodeView episode) _ = PlayEpisodeAsync(episode);", detail);
        Assert.Contains("if (e.ClickedItem is EpisodeView view) await PlayEpisodeAsync(view);", detail);
        Assert.Contains("private Task PlayEpisodeAsync(EpisodeView view) => OpenPlayerAsync(view.Episode.ContentId, null, fromEpisode: true);", detail);
    }

    [Fact]
    public void ContinueWatchingPlayStartsTheCardWithoutDetail()
    {
        var browse = Read("BrowsePage.cs.txt");
        Assert.Contains("_cardMenu.PlayRequested += OnCardPlayRequested;", browse);
        var play = Section(browse, "private async void OnCardPlayRequested(", "private void OnCardChanged(");
        // Home pauses before the player takes the frame, as OpenCard does for Detail, and never pushes over a page
        // that opened while the target loaded.
        Assert.True(play.IndexOf("PauseHomeForNavigation();", StringComparison.Ordinal)
            < play.IndexOf("ShellPage.Current?.OpenPlayer(request);", StringComparison.Ordinal));
        Assert.Contains("if (!_isLoaded || !IsHome || !_homeActive", play);
        Assert.DoesNotContain("OpenDetail", play);
        var shell = Read("ShellPage.cs.txt");
        Assert.Contains("internal void OpenPlayer(PlayerRequest request)", shell);
        Assert.Contains("Frame.Navigate(typeof(PlayerPage), request);", shell);
    }

    private static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, name)).ReplaceLineEndings("\n");

    private static string Section(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"missing {start}");
        var to = text.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, $"missing {end}");
        return text[from..to];
    }
}
