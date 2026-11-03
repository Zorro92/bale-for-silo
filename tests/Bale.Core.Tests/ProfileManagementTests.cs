using System.Net;
using System.Text.Json;
using Bale.Core.Api.Models;
using Bale.Core.Connection;
using Bale.Core.Profiles;

namespace Bale.Core.Tests;

public class ProfileManagementTests
{
    private static readonly Profile Primary = new() { Id = "primary", Name = "Parent", IsPrimary = true, HasPin = true };
    private const string Stale = """{"type":"https://siloserver.org/docs/api/v2/problems/profile_verification_required","title":"Verify profile","status":403,"detail":"Verify again"}""";
    private const string Saved = """{"id":"child","name":"Kids","has_pin":false,"is_child":true}""";

    private static SiloClient Create(StubHandler stub)
    {
        var client = new SiloClient(new Uri("https://media.example.com/"), new ClientIdentity("d", "Test Xbox", "xbox", "Bale"), stub);
        client.RestoreSession("refresh", "access", DateTimeOffset.UtcNow.AddHours(1), "viewer", "viewer-proof");
        return client;
    }

    [Fact]
    public void ManageGateRequiresAPrimaryNotAnAdminAccount()
    {
        var session = new HouseholdManagementSession((_, _, _) => Task.FromResult<string?>(null), () => 1);
        var gate = new ProfileManagementGate(session);
        Assert.False(ProfileManagementGate.CanManage([new Profile { Id = "a" }, new Profile { Id = "b" }]));
        Assert.Equal(ProfileManagementAction.None, gate.Request([]));
        Assert.True(ProfileManagementGate.CanManage([new Profile { Id = "only" }]));
        Assert.Equal(ProfileManagementAction.EnterPin, gate.Request([Primary], add: true));
        Assert.False(gate.IsManaging);
        Assert.Equal(ProfileManagementAction.None, gate.CompletePin(" "));
        Assert.Equal(ProfileManagementAction.Create, gate.CompletePin("proof"));
        Assert.True(gate.IsManaging);
        Assert.Equal(ProfileManagementAction.Create, gate.Request([Primary], add: true));
        gate.Done();
        Assert.False(session.IsActive);
        Assert.False(ProfileManagementGate.CanDelete(Primary));
        Assert.True(ProfileManagementGate.CanDelete(new Profile { Id = "child" }));
    }

    [Fact]
    public void CancelledGateDoesNotStartManageModeOrRememberAPendingAdd()
    {
        var session = new HouseholdManagementSession((_, _, _) => Task.FromResult<string?>(null), () => 1);
        var gate = new ProfileManagementGate(session);
        gate.Request([Primary], add: true);
        gate.CancelPin();
        Assert.Equal(ProfileManagementAction.None, gate.CompletePin("late"));
        gate.Request([Primary]);
        Assert.Equal(ProfileManagementAction.Manage, gate.CompletePin("proof"));
    }

    [Fact]
    public void UnlockedPrimaryEntersWithoutSelectingIt()
    {
        var session = new HouseholdManagementSession((_, _, _) => Task.FromResult<string?>(null), () => 1);
        var gate = new ProfileManagementGate(session);
        Assert.Equal(ProfileManagementAction.Manage, gate.Request([new Profile { Id = "only" }]));
        Assert.True(gate.IsManaging);
        Assert.Throws<InvalidOperationException>(() => session.Begin(Primary, null));
    }

    [Fact]
    public async Task ManagerHeadersReplaceTheViewerButNeverChangeItsSelection()
    {
        var stub = new StubHandler().Enqueue(HttpStatusCode.Created, Saved).Enqueue(Saved).Enqueue("""{"items":[]}""");
        using var client = Create(stub);
        client.Profiles.Management.Begin(Primary, "manager-proof");
        await client.Profiles.CreateAsync(new ProfileForm { Name = "Kids" }.BuildCreate());
        await client.Profiles.UpdateAsync("child", new ProfileForm(new Profile { Id = "child", Name = "Kids" }).BuildUpdate());
        await client.GetProfilesAsync();
        Assert.Equal("primary", stub.Requests[0].Headers.GetValues("X-Profile-Id").Single());
        Assert.Equal("manager-proof", stub.Requests[1].Headers.GetValues("X-Profile-Token").Single());
        Assert.Equal(HttpMethod.Patch, stub.Requests[1].Method);
        Assert.Equal("viewer", stub.Requests[2].Headers.GetValues("X-Profile-Id").Single());
        Assert.Equal("viewer", client.ProfileId);
        Assert.Equal("viewer-proof", client.ProfileToken);
    }

    [Fact]
    public async Task ExplicitPinAndRatingClearsAreNullOnV2NotEmptyOrOmitted()
    {
        var stub = new StubHandler().Enqueue(Saved);
        using var client = Create(stub);
        client.Profiles.Management.Begin(Primary, "proof");
        var form = new ProfileForm(new Profile { Id = "child", Name = "Kids", HasPin = true, MaxContentRating = "PG" });
        form.TogglePin(false);
        form.Access = form.Access.WithRating(null);
        await client.Profiles.UpdateAsync("child", form.BuildUpdate());
        using var json = JsonDocument.Parse(stub.RequestBodies[0]!);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("pin").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("max_content_rating").ValueKind);
        Assert.False(json.RootElement.TryGetProperty("allowed_library_ids", out _));
        Assert.False(json.RootElement.TryGetProperty("max_advisory_age", out _));
        Assert.False(json.RootElement.TryGetProperty("max_playback_quality", out _));
    }

    [Fact]
    public async Task UnchangedPinIsAbsentAndNewPinIsAString()
    {
        var stub = new StubHandler().Enqueue(Saved).Enqueue(Saved);
        using var client = Create(stub);
        client.Profiles.Management.Begin(Primary, "proof");
        var form = new ProfileForm(new Profile { Id = "child", Name = "Kids", HasPin = true });
        await client.Profiles.UpdateAsync("child", form.BuildUpdate());
        form.ConfirmPin("9876");
        await client.Profiles.UpdateAsync("child", form.BuildUpdate());
        using var first = JsonDocument.Parse(stub.RequestBodies[0]!);
        using var second = JsonDocument.Parse(stub.RequestBodies[1]!);
        Assert.False(first.RootElement.TryGetProperty("pin", out _));
        Assert.Equal("9876", second.RootElement.GetProperty("pin").GetString());
    }

    [Fact]
    public async Task LibraryAndPinRequestsHaveNoProfileHeadersEvenWithAStaleViewer()
    {
        var stub = new StubHandler().Enqueue("""{"items":[{"id":"b","sort_order":2},{"id":"a","sort_order":1}]}""")
            .Enqueue("""{"valid":true,"profile_token":"primary-proof"}""");
        using var client = Create(stub);
        Assert.Equal(new[] { "a", "b" }, (await client.Profiles.ListLibrariesAsync()).Select(library => library.Id));
        Assert.Equal("primary-proof", await client.Profiles.VerifyPinAsync("primary", "1234"));
        Assert.All(stub.Requests, request =>
        {
            Assert.False(request.Headers.Contains("X-Profile-Id"));
            Assert.False(request.Headers.Contains("X-Profile-Token"));
            Assert.Equal("Bearer access", request.Headers.Authorization!.ToString());
        });
        Assert.Equal("viewer", client.ProfileId);
    }

    [Fact]
    public async Task ManagerOverrideSurvivesACentralBearerRefresh()
    {
        var stub = new StubHandler().Enqueue(HttpStatusCode.Unauthorized, """{"status":401,"title":"Unauthorized"}""")
            .Enqueue("""{"access_token":"fresh-access","refresh_token":"fresh-refresh","expires_in":3600}""")
            .Enqueue(Saved);
        using var client = Create(stub);
        client.Profiles.Management.Begin(Primary, "proof");
        await client.Profiles.UpdateAsync("child", new ProfilePatch { Name = "Kids" });
        Assert.Equal("Bearer fresh-access", stub.Requests[2].Headers.Authorization!.ToString());
        Assert.Equal("primary", stub.Requests[2].Headers.GetValues("X-Profile-Id").Single());
        Assert.Equal("proof", stub.Requests[2].Headers.GetValues("X-Profile-Token").Single());
    }

    [Fact]
    public async Task StaleManagerReasksWrongPinThenRetriesTheRefusedWriteOnce()
    {
        var stub = new StubHandler().Enqueue(HttpStatusCode.Forbidden, Stale)
            .Enqueue("""{"valid":false}""").Enqueue("""{"valid":true,"profile_token":"fresh"}""").Enqueue(Saved);
        using var client = Create(stub);
        var session = client.Profiles.Management;
        session.Begin(Primary, "old");
        var prompted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Changed += () => { if (session.Reverify is not null) prompted.TrySetResult(); };
        var save = client.Profiles.UpdateAsync("child", new ProfilePatch { Name = "Kids" });
        await prompted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.SubmitPinAsync("0000");
        Assert.Equal("Wrong PIN. Try again.", session.Reverify?.Error);
        await session.SubmitPinAsync("1234");
        Assert.Equal("child", (await save).Id);
        Assert.Null(session.Reverify);
        Assert.True(session.IsActive);
        Assert.Equal("fresh", stub.Requests.Last().Headers.GetValues("X-Profile-Token").Single());
        Assert.Equal(stub.RequestBodies[0], stub.RequestBodies[3]);
        Assert.Equal("viewer", client.ProfileId);
    }

    [Fact]
    public async Task CancelReverificationEndsManageModeWithoutASecondWrite()
    {
        var stub = new StubHandler().Enqueue(HttpStatusCode.Forbidden, Stale);
        using var client = Create(stub);
        var session = client.Profiles.Management;
        session.Begin(Primary, "old");
        var prompted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Changed += () => { if (session.Reverify is not null) prompted.TrySetResult(); };
        var save = client.Profiles.UpdateAsync("child", new ProfilePatch { Name = "Kids" });
        await prompted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.End();
        await Assert.ThrowsAsync<InvalidOperationException>(() => save);
        Assert.False(session.IsActive);
        Assert.Single(stub.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task OtherFailuresAreNotReplayedOrReverified(HttpStatusCode status)
    {
        var stub = new StubHandler().Enqueue(status, """{"status":403,"type":"https://siloserver.org/docs/api/v2/problems/forbidden","detail":"Not allowed"}""");
        using var client = Create(stub);
        client.Profiles.Management.Begin(Primary, "proof");
        var error = await Assert.ThrowsAsync<Problem>(() => client.Profiles.CreateAsync(new ProfileCreate { Name = "Kids" }));
        Assert.Equal("Not allowed", ProfileRepository.ErrorMessage(error, "Failed"));
        Assert.Null(client.Profiles.Management.Reverify);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task DeletingTheSelectedProfileClearsOnlySelectionBeforeTheNextRead()
    {
        var stub = new StubHandler().Enqueue(HttpStatusCode.NoContent, "").Enqueue("""{"items":[]}""");
        using var client = Create(stub);
        client.Profiles.Management.Begin(Primary, "proof");
        await client.Profiles.DeleteAsync("viewer");
        Assert.Null(client.ProfileId);
        Assert.Null(client.ProfileToken);
        Assert.True(client.Profiles.Management.IsActive);
        await client.GetProfilesAsync();
        Assert.False(stub.Requests[1].Headers.Contains("X-Profile-Id"));
    }

    [Fact]
    public async Task AccountReplacementDropsManageProofBeforeAnyWrite()
    {
        var stub = new StubHandler();
        using var client = Create(stub);
        client.Profiles.Management.Begin(Primary, "proof");
        client.SignIn(new TokenPair { AccessToken = "new-account", RefreshToken = "new-refresh" });
        Assert.False(client.Profiles.Management.IsActive);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.Profiles.CreateAsync(new ProfileCreate { Name = "Kids" }));
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task CancelDuringPinVerificationNeverRenewsOrRetries()
    {
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new HouseholdManagementSession((_, _, _) => answer.Task, () => 1);
        session.Begin(Primary, "old");
        var calls = 0;
        var prompted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Changed += () => { if (session.Reverify is not null) prompted.TrySetResult(); };
        var save = session.RunAsync<bool>((_, _, _) => { calls++; throw new Problem { ResponseStatusCode = 403, Type = "https://siloserver.org/profile_verification_required" }; }, default);
        await prompted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var checking = session.SubmitPinAsync("1234");
        session.End();
        answer.SetResult("late-proof");
        await checking;
        await Assert.ThrowsAsync<InvalidOperationException>(() => save);
        Assert.False(session.IsActive);
        Assert.Equal(1, calls);
    }
}
