using Bale.Core.Api.Models;
using Bale.Core.Browse;

namespace Bale.Core.Tests;

public class HomeFeedbackTests
{
    [Theory]
    [InlineData(true, 0, null, HomeFeedStatus.Loading)]
    [InlineData(true, 0, "old error", HomeFeedStatus.Loading)]
    [InlineData(false, 0, null, HomeFeedStatus.Empty)]
    [InlineData(false, 0, "failure", HomeFeedStatus.Error)]
    [InlineData(true, 1, null, HomeFeedStatus.Content)]
    [InlineData(false, 1, "quiet refresh failed", HomeFeedStatus.Content)]
    public void OnlyAnIdleSuccessfulEmptyFeedShowsTheEmptyState(bool loading, int rows, string? error, HomeFeedStatus expected) =>
        Assert.Equal(expected, HomeFeedback.Status(loading, rows, error));

    [Fact]
    public void FeedbackCopyMatchesHomeRatherThanSearch()
    {
        Assert.Equal("Nothing to watch yet", HomeFeedback.EmptyTitle);
        Assert.Equal("Add media to your libraries or start watching to see it here.", HomeFeedback.EmptyMessage);
        Assert.Equal("Refresh", HomeFeedback.RefreshLabel);
        Assert.Equal("Retry", HomeFeedback.RetryLabel);
    }

    [Fact]
    public void HiddenHomeOffersSettingsInsteadOfRefreshingHiddenRows()
    {
        Assert.Equal("Home sections are hidden", HomeFeedback.HiddenTitle);
        Assert.Equal("Choose which rows appear in Settings → General → Home Sections.", HomeFeedback.HiddenMessage);
        Assert.Equal("Open Settings", HomeFeedback.OpenSettingsLabel);
        Assert.NotEqual(HomeFeedback.RefreshLabel, HomeFeedback.OpenSettingsLabel);
    }

    [Fact]
    public void ApiFeedbackUsesTheServerMessageOrTheHomeFallback()
    {
        Assert.Equal("Home is unavailable", HomeFeedback.ErrorMessage(new Problem { Detail = "Home is unavailable", Title = "Failure" }));
        Assert.Equal("Failure", HomeFeedback.ErrorMessage(new Problem { Detail = " ", Title = "Failure" }));
        Assert.Equal("Failed to load home sections", HomeFeedback.ErrorMessage(new Problem { Detail = "", Title = " " }));
    }

    [Fact]
    public void NetworkAndUnexpectedExceptionsNeverExposeTheirRawMessages()
    {
        foreach (var error in new Exception[]
        {
            new HttpRequestException("private request details"), new TaskCanceledException("private timeout details"),
            new TimeoutException("private timeout details"),
        })
        {
            Assert.Equal("Network error. Check your connection.", HomeFeedback.ErrorMessage(error));
        }

        Assert.Equal("Failed to load home sections", HomeFeedback.ErrorMessage(new InvalidDataException("private response details")));
    }
}
