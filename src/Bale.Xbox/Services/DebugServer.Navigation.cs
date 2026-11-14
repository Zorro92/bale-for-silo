#if DEBUG_SERVER
using Bale.Core.Detail;
using Bale.Xbox.Views;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace Bale.Xbox.Services;

internal sealed partial class DebugServer
{
    private static readonly SemaphoreSlim NavigationGate = new(1, 1);

    internal static void RecordNavigation(NavigationEventArgs e)
    {
        var page = e.SourcePageType.Name;
        var parameters = e.Parameter switch
        {
            PlayerRequest player => $"content={DebugNavigationHistory.Identity(player.Detail.ContentId)} file={DebugNavigationHistory.Identity(player.Selection.FileId ?? "auto")}",
            SeriesDetailEntry series => $"content={DebugNavigationHistory.Identity(series.SeriesId)} episode={DebugNavigationHistory.Identity(series.EpisodeId ?? "none")} season={series.SeasonNumber}",
            LibraryRecommendedEntry library => $"library={DebugNavigationHistory.Identity(library.LibraryId)}",
            PersonPageEntry person => $"person={DebugNavigationHistory.Identity(person.PersonId)}",
            RequestDetailEntry item => $"type={DebugNavigationHistory.Identity(item.MediaType)} tmdb={item.TmdbId}",
            string id when e.SourcePageType == typeof(DetailPage) => $"content={DebugNavigationHistory.Identity(id)}",
            _ when e.SourcePageType == typeof(DetailPage) => DetailPage.DebugHistoryParameters(e.Parameter),
            string root when e.SourcePageType == typeof(BrowsePage) && root is "home" or "foryou" => root,
            _ => string.Empty,
        };
        DebugNavigationHistory.Shared.Add("page", $"{page} mode={e.NavigationMode} {parameters}".TrimEnd());
    }

    private static string NavigationFocus() => FocusManager.GetFocusedElement() is FrameworkElement element
        ? DebugNavigationHistory.Identity(element.GetType().Name) + "#" + DebugNavigationHistory.Identity(element.Name)
        : "none";

    private static async Task RecordCommandAsync(string path, IReadOnlyDictionary<string, string> query, string status)
    {
        var command = path switch
        {
            "/type" => $"type characters={query.GetValueOrDefault("text")?.Length ?? 0}",
            "/goto" => $"goto characters={query.GetValueOrDefault("target")?.Length ?? 0}",
            "/open" or "/play" => path.TrimStart('/') + " content=" + DebugNavigationHistory.Identity(query.GetValueOrDefault("id")),
            "/home" => "home",
            _ when path.StartsWith("/key/", StringComparison.Ordinal) => "key " + DebugNavigationHistory.Identity(path[5..]),
            _ when path.StartsWith("/press/", StringComparison.Ordinal) => "press " + DebugNavigationHistory.Identity(path[7..]),
            _ => null,
        };
        if (command is null) return;
        var focus = await OnFfmpegUiAsync(NavigationFocus);
        DebugNavigationHistory.Shared.Add("command", $"{command} HTTP={status.Split(' ')[0]} focus={focus}");
    }

    private static async Task<(string Status, string Body)> NavigateDebugAsync(DebugNavigationRequest? request, bool play = false)
    {
        if (!NavigationGate.Wait(0)) return ("409 Conflict", "another debug navigation is in progress\n");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(play ? 90 : 30));
        try
        {
            var operation = await OnFfmpegUiAsync(() => NavigateOnUiAsync(request, play, deadline.Token));
            return await operation;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return ("408 Request Timeout", "debug navigation timed out\n" + await OnFfmpegUiAsync(NavigationSummary));
        }
        catch (Exception ex)
        {
            return ("500 Internal Server Error", $"debug navigation failed hr=0x{unchecked((uint)ex.HResult):X8}\n");
        }
        finally { NavigationGate.Release(); }
    }

    private static string NavigationSummary()
    {
        var summary = "Page: " + DescribePage() + "\n";
        if (CurrentPage(out _) is PlayerPage player)
            return summary + "Player: " + player.DebugPlayback + "\n" + player.DebugPlanDelivery;
        return summary + "Focus: " + DescribeFocus() + "\n";
    }

    private static async Task<(string Status, string Body)> NavigateOnUiAsync(DebugNavigationRequest? request, bool play, CancellationToken cancellation)
    {
        // Never choose a profile or use a saved profile on behalf of the test.
        if (Session.Client is not { IsSignedIn: true, ProfileId: { Length: > 0 } profile } client)
            return ("409 Conflict", "no profile selected\n");
        if (Window.Current.Content is not Frame frame)
            return ("409 Conflict", "no navigation frame\n");

        bool SameProfile() => ReferenceEquals(Session.Client, client) && client.ProfileId == profile;
        if (frame.Content is PlayerPage previous)
        {
            await previous.DebugStopForNavigationAsync().WaitAsync(cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (!SameProfile() || !ReferenceEquals(frame.Content, previous))
                return ("409 Conflict", "navigation or profile changed while stopping the player\n");
        }
        if (!SameProfile()) return ("409 Conflict", "profile changed during navigation\n");
        cancellation.ThrowIfCancellationRequested();

        if (request is null)
        {
            if (!frame.Navigate(typeof(ShellPage))) return ("500 Internal Server Error", "Home navigation failed\n");
            frame.BackStack.Clear();
            frame.ForwardStack.Clear();
            var shell = frame.Content as ShellPage;
            while (SameProfile() && ReferenceEquals(frame.Content, shell))
            {
                cancellation.ThrowIfCancellationRequested();
                if (shell?.DebugHomeReady == true && !PageFrame.IsTransitioning)
                    return ("200 OK", NavigationSummary() + "Back stack: empty\n");
                await Task.Delay(50, cancellation);
            }
            return ("409 Conflict", "Home navigation was superseded\n");
        }

        var wasPlayer = frame.Content is PlayerPage;
        // Same full-screen root Frame and content-id parameter as ShellPage.OpenDetail(card.ContentId).
        if (!frame.Navigate(typeof(DetailPage), request.ContentId) || frame.Content is not DetailPage detail)
            return ("500 Internal Server Error", "Detail navigation failed\n");
        // A stopped player must never become a Back destination that starts itself again.
        if (wasPlayer && frame.BackStack.Count > 0) frame.BackStack.RemoveAt(frame.BackStack.Count - 1);
        while (SameProfile() && ReferenceEquals(frame.Content, detail))
        {
            cancellation.ThrowIfCancellationRequested();
            if (detail.DebugNavigationState == "not-found") return ("404 Not Found", "unknown content id\n");
            if (detail.DebugNavigationState == "failed") return ("500 Internal Server Error", "Detail load failed\n");
            if (detail.DebugNavigationState == "loaded" && detail.IsLoaded && !PageFrame.IsTransitioning)
            {
                var selection = detail.DebugSelectPlayback(request);
                if (selection.Status != "200 OK") return selection;
                if (!play) return ("200 OK", NavigationSummary() + "Status: loaded\n" + detail.DebugSelection);
                if (OpenPopups().Count > 0) return ("409 Conflict", "dismiss the open overlay before playback\n");
                cancellation.ThrowIfCancellationRequested();
                await detail.DebugOpenPlayerAsync(request).WaitAsync(cancellation);
                if (!SameProfile()) return ("409 Conflict", "profile changed during playback handoff\n");
                if (frame.Content is not PlayerPage player) return ("409 Conflict", "Detail Play handoff failed; no player opened\n");
                while (SameProfile() && ReferenceEquals(frame.Content, player))
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (player.DebugNavigationFailed) return ("500 Internal Server Error", NavigationSummary() + "Playback failed\n");
                    if (player.IsLoaded && player.DebugNavigationReady && !PageFrame.IsTransitioning)
                        return ("200 OK", NavigationSummary());
                    await Task.Delay(50, cancellation);
                }
                return ("409 Conflict", "player navigation was superseded\n");
            }
            await Task.Delay(50, cancellation);
        }
        return ("409 Conflict", "Detail navigation was superseded\n");
    }
}
#endif
