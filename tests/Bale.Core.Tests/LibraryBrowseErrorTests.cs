using Bale.Core.Api.Models;
using Bale.Core.Browse;
using Microsoft.Kiota.Abstractions;

namespace Bale.Core.Tests;

public class LibraryBrowseErrorTests
{
    [Theory]
    [InlineData("The selected genre is unavailable.", "Invalid query", "The selected genre is unavailable.")]
    [InlineData(null, "Invalid query", "Invalid query")]
    [InlineData(" ", "Invalid query", "Invalid query")]
    [InlineData(null, null, "Failed to load library")]
    [InlineData("", "", "Failed to load library")]
    [InlineData(" \t", " \n", "Failed to load library")]
    public void ServerErrorsUseProblemDetailThenTitleOrLibraryFallback(string? detail, string? title, string expected) =>
        Assert.Equal(expected, LibraryBrowseFeed.ErrorMessage(new Problem { Detail = detail, Title = title, ResponseStatusCode = 400 }));

    [Fact]
    public void UnparsedApiErrorsUseLibraryFallbackRatherThanKiotaDiagnostics() =>
        Assert.Equal("Failed to load library", LibraryBrowseFeed.ErrorMessage(new ApiException("No error factory is registered.") { ResponseStatusCode = 502 }));

    [Theory]
    [InlineData("Injected catalog failure (fault catalog).", "Network error: Injected catalog failure (fault catalog).")]
    [InlineData("Connection refused", "Network error: Connection refused")]
    [InlineData("", "Network error: unknown")]
    [InlineData(" \t", "Network error: unknown")]
    public void HttpFailuresPrefixTheExceptionMessage(string message, string expected) =>
        Assert.Equal(expected, LibraryBrowseFeed.ErrorMessage(new HttpRequestException(message)));

    [Fact]
    public void OtherFailuresWithoutAResponseUseNetworkCopy()
    {
        Exception[] errors = [new IOException("Connection closed"), new TimeoutException("Timed out"), new Exception("Offline")];
        foreach (var error in errors)
        {
            Assert.Equal($"Network error: {error.Message}", LibraryBrowseFeed.ErrorMessage(error));
        }
    }
}
