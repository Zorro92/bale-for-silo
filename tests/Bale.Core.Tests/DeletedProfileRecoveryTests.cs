using System.Net;
using Bale.Core.Api.Models;
using Bale.Core.Connection;
using Bale.Core.Profiles;

namespace Bale.Core.Tests;

/// <summary>Apple b5c73e3a: Home's not_found while a profile is active re-reads the list; only a missing profile recovers.</summary>
public class DeletedProfileRecoveryTests
{
    private const string NotFound = """{"type":"https://siloserver.org/docs/api/v2/problems/not_found","title":"Not found","status":404}""";
    private const string WithViewer = """{"items":[{"id":"primary"},{"id":"viewer"}]}""";
    private const string WithoutViewer = """{"items":[{"id":"primary"}]}""";

    private static SiloClient Create(StubHandler stub)
    {
        var client = new SiloClient(new Uri("https://media.example.com/"), new ClientIdentity("d", "Test Xbox", "xbox", "Bale"), stub);
        client.RestoreSession("refresh", "access", DateTimeOffset.UtcNow.AddHours(1), "viewer", "viewer-proof");
        return client;
    }

    private static Problem Problem(int status, string type) => new()
    {
        ResponseStatusCode = status, Status = status, Type = "https://siloserver.org/docs/api/v2/problems/" + type,
    };

    [Fact]
    public async Task HomesNotFoundProblemIsRecognised()
    {
        var stub = new StubHandler().Enqueue(HttpStatusCode.NotFound, NotFound);
        using var client = Create(stub);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => client.Api.Api.V2.Home.Sections.GetAsync());
        Assert.True(DeletedProfileRecovery.IsNotFound(error));
        // Home itself still names the acting profile; only the list read drops it.
        Assert.Equal("viewer", stub.Requests[0].Headers.GetValues("X-Profile-Id").Single());
    }

    [Fact]
    public void OnlyA404NotFoundCounts()
    {
        Assert.True(DeletedProfileRecovery.IsNotFound(Problem(404, "not_found")));
        Assert.False(DeletedProfileRecovery.IsNotFound(Problem(404, "library_not_found")));
        Assert.False(DeletedProfileRecovery.IsNotFound(Problem(403, "not_found")));
        Assert.False(DeletedProfileRecovery.IsNotFound(new HttpRequestException("offline")));
    }

    [Fact]
    public void TheActiveProfileIsMissingOnlyFromAListWithoutIt()
    {
        Profile[] list = [new() { Id = "primary" }, new() { Id = "viewer" }];
        Assert.False(DeletedProfileRecovery.ActiveProfileMissing(list, "viewer"));
        Assert.True(DeletedProfileRecovery.ActiveProfileMissing(list, "gone"));
        Assert.True(DeletedProfileRecovery.ActiveProfileMissing([], "viewer"));
        Assert.False(DeletedProfileRecovery.ActiveProfileMissing(list, null));
    }

    [Fact]
    public async Task ADeletedProfileIsFoundByReadingTheListWithoutIt()
    {
        var stub = new StubHandler().Enqueue(WithoutViewer);
        using var client = Create(stub);
        Assert.True(await client.ActiveProfileWasDeletedAsync(Problem(404, "not_found"), "viewer"));
        var list = Assert.Single(stub.Requests);
        Assert.EndsWith("/api/v2/profiles", list.RequestUri!.AbsolutePath);
        Assert.False(list.Headers.Contains("X-Profile-Id"));
        Assert.Equal("viewer-proof", list.Headers.GetValues("X-Profile-Token").Single());
    }

    [Fact]
    public async Task AValidProfileStaysAfterAnotherNotFound()
    {
        var stub = new StubHandler().Enqueue(WithViewer);
        using var client = Create(stub);
        Assert.False(await client.ActiveProfileWasDeletedAsync(Problem(404, "not_found"), "viewer"));
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task OtherFailuresNeverReadTheList()
    {
        var stub = new StubHandler();
        using var client = Create(stub);
        Assert.False(await client.ActiveProfileWasDeletedAsync(Problem(404, "item_not_found"), "viewer"));
        Assert.False(await client.ActiveProfileWasDeletedAsync(Problem(500, "internal"), "viewer"));
        Assert.False(await client.ActiveProfileWasDeletedAsync(new HttpRequestException("offline"), "viewer"));
        Assert.False(await client.ActiveProfileWasDeletedAsync(Problem(404, "not_found"), null));
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task AFailedReReadOrANewerChoiceLeavesTheProfile()
    {
        var failing = new StubHandler().Enqueue(HttpStatusCode.InternalServerError, """{"status":500,"title":"Error"}""");
        using (var client = Create(failing))
        {
            Assert.False(await client.ActiveProfileWasDeletedAsync(Problem(404, "not_found"), "viewer"));
        }

        // The request was made as an earlier profile; the viewer chose another one meanwhile.
        var stub = new StubHandler().Enqueue(WithoutViewer);
        using var switched = Create(stub);
        Assert.False(await switched.ActiveProfileWasDeletedAsync(Problem(404, "not_found"), "earlier"));
    }
}
