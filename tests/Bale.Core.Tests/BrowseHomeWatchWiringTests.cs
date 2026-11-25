namespace Bale.Core.Tests;

/// <summary>
/// How Xbox Home holds its events socket and retries a transient read (BrowsePage.Home.cs). The page compiles only on
/// Windows, so its wiring is pinned by source here; HomeWatchState and HomeFeedState are tested directly.
/// </summary>
public class BrowseHomeWatchWiringTests
{
    private static string Home() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "BrowsePage.Home.cs.txt")).ReplaceLineEndings("\n");

    private static string Method(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        var next = source.IndexOf("\n    private ", start + signature.Length, StringComparison.Ordinal);
        return next < 0 ? source[start..] : source[start..next];
    }

    [Fact]
    public void TheSocketRunsOnlyWhileHomeIsShown()
    {
        var home = Home();
        var show = Method(home, "private void ShowHome()");
        Assert.True(show.IndexOf("_homeState.OnShown(", StringComparison.Ordinal) < show.IndexOf("StartHomeWatch();", StringComparison.Ordinal));
        Assert.Contains("if (!_homeActive || _homeSuspended || _homeWindow?.Visible != true) return;", show);
        // Every way Home leaves the screen goes through HideHome: navigation, the window, suspension, unloading.
        var hide = Method(home, "private void HideHome(bool restoringFocus = false)");
        Assert.True(hide.IndexOf("StopHomeWatch();", StringComparison.Ordinal) < hide.IndexOf("_homeState.OnHidden();", StringComparison.Ordinal));
        Assert.Contains("HideHome(restoringFocus: true);", Method(home, "private void DetachHomeLifecycle()"));
        Assert.Contains("HideHome(restoringFocus: true);", Method(home, "internal void PauseHomeForNavigation()"));
        Assert.Contains("HideHome();", Method(home, "private void OnHomeSuspending("));
        Assert.Contains("else HideHome();", Method(home, "private void OnHomeVisibilityChanged("));
        Assert.Contains("_homeWatch?.Dispose();\n        _homeWatch = null;", Method(home, "private void StopHomeWatch()"));
    }

    [Fact]
    public void ASignalBecomesAQuietRefreshForTheSameSessionAndProfileOnly()
    {
        var home = Home();
        var start = Method(home, "private void StartHomeWatch()");
        Assert.Contains("Session.Client is not { ProfileId: { Length: > 0 } profileId } client", start);
        Assert.Contains("Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => OnHomeWatchRefresh(watch!, client, profileId))", start);
        Assert.Contains("watch.Start();", start);
        var refresh = Method(home, "private void OnHomeWatchRefresh(");
        Assert.Contains("if (!ReferenceEquals(_homeWatch, watch)) return;", refresh);
        Assert.Contains("if (!IsHome || !ReferenceEquals(client, Session.Client) || client.ProfileId != profileId)", refresh);
        Assert.Contains("StartHomeLoad(_homeState.RequestRefresh());", refresh);
        Assert.Contains("catch (Exception error)", refresh);
    }

    [Fact]
    public void AFailedQuietReadWaitsForItsRetryBeforeAnyPendingRefresh()
    {
        var home = Home();
        Assert.Contains("if (OwnsHome()) ContinueHome(client, profileId);", home);
        Assert.DoesNotContain("if (OwnsHome()) StartHomeLoad(_homeState.TakePendingRefresh());", home);
        var next = Method(home, "private void ContinueHome(");
        Assert.Contains("if (_homeState.PendingRetry is { } retry) _ = RetryHomeAsync(retry, client, profileId);", next);
        Assert.Contains("else StartHomeLoad(_homeState.TakePendingRefresh());", next);
        var retry = Method(home, "private async Task RetryHomeAsync(");
        Assert.True(retry.IndexOf("await Task.Delay(retry.Delay);", StringComparison.Ordinal)
            < retry.IndexOf("StartHomeLoad(_homeState.StartRetry(retry));", StringComparison.Ordinal));
    }
}
