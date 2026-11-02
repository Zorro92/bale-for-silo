using Bale.Core.Api.Models;

namespace Bale.Core.Browse;

public enum HomeFeedStatus { Loading, Content, Empty, Error }

/// <summary>screens/home/TvHomeScreen.kt and components/StatusViews.kt, not Search's feedback copy.</summary>
public static class HomeFeedback
{
    public const string EmptyTitle = "Nothing to watch yet";
    public const string EmptyMessage = "Add media to your libraries or start watching to see it here.";
    public const string RefreshLabel = "Refresh";
    public const string RetryLabel = "Retry";

    // TvHomeScreen.kt only replaces the feed with loading/error feedback when no sections survive.
    public static HomeFeedStatus Status(bool isLoading, int visibleRowCount, string? error) => visibleRowCount > 0
        ? HomeFeedStatus.Content : isLoading ? HomeFeedStatus.Loading
        : error is not null ? HomeFeedStatus.Error : HomeFeedStatus.Empty;

    // HomeViewModel.kt: show API feedback or the fixed network message, never a raw exception/URL.
    public static string ErrorMessage(Exception error) => error switch
    {
        Problem { Detail: { } detail } when !string.IsNullOrWhiteSpace(detail) => detail,
        Problem { Title: { } title } when !string.IsNullOrWhiteSpace(title) => title,
        HttpRequestException or OperationCanceledException or TimeoutException => "Network error. Check your connection.",
        _ => "Failed to load home sections",
    };
}
