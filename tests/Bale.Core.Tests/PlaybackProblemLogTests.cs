using Bale.Core.Api.Models;
using Bale.Core.Playback;

namespace Bale.Core.Tests;

public class PlaybackProblemLogTests
{
    [Fact]
    public void LogsOnlyKnownProblemMetadataAndOwningValidationFields()
    {
        var problem = new Problem
        {
            ResponseStatusCode = 422, Status = 400,
            Type = "https://siloserver.org/docs/api/v2/problems/validation_failed", Title = "Validation failed",
            Detail = "https://private.invalid/?token=secret", Instance = "https://private.invalid/api_key=secret",
            Errors = [new() { Location = "body.attempt_count", Code = "out_of_range", Detail = "secret" },
                new() { Location = "body.client_playback_context.deliveries.secret", Code = "invalid", Detail = "secret" }],
        };
        Assert.Equal("http=422 problem=validation_failed title=\"Validation failed\" fields=[body.attempt_count:out_of_range,body.client_playback_context:invalid]",
            PlaybackProblemLog.Describe(problem));
    }

    [Theory]
    [InlineData("https://siloserver.org/docs/api/v2/problems/validation_failed?token=secret", "Validation failed")]
    [InlineData("https://private.invalid/secret", "secret")]
    [InlineData("secret", "secret")]
    public void UnknownTypesAndTitlesAreNotPrinted(string type, string title)
    {
        var log = PlaybackProblemLog.Describe(new()
        {
            Status = 422, Type = type, Title = title,
            Errors = [new() { Location = "body.secret", Code = "secret" }, new() { Location = "https://private.invalid/secret", Code = "secret" }],
        });
        Assert.Equal("http=422 problem=? title=\"?\" fields=[?:?]", log);
    }

    [Fact]
    public void ARecognizedTypeDoesNotAuthorizeAnArbitraryTitleOrUnboundedErrorList()
    {
        var log = PlaybackProblemLog.Describe(new()
        {
            ResponseStatusCode = 409, Type = "https://siloserver.org/docs/api/v2/problems/conflict", Title = "secret",
            Errors = Enumerable.Range(0, 100).Select(i => new ProblemError { Location = i < 8 ? "body" : "body.attempt_count", Code = "invalid" }).ToList(),
        });
        Assert.Equal("http=409 problem=conflict title=\"?\" fields=[body:invalid]", log);
    }
}
