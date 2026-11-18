using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Bale.Core.Connection;

namespace Bale.Core.Tests;

public class AccountProfilesRefresherTests
{
    private const string Unauthorized = """{"title":"Unauthorized","status":401}""";

    private sealed class Storage : ISavedServerStorage
    {
        public string? Json;
        public string? Read() => Json;
        public void Write(string json) => Json = json;
    }

    private sealed class Vault : IAccountCredentials
    {
        public readonly ConcurrentDictionary<string, (string Refresh, string? Access)> Slots = new();
        public int Saves;
        public string? LoadRefreshToken(SavedAccount account) => Slots.TryGetValue(account.CredentialKey, out var slot) ? slot.Refresh : null;
        public string? LoadAccessToken(SavedAccount account) => Slots.TryGetValue(account.CredentialKey, out var slot) ? slot.Access : null;
        public void SaveTokens(SavedAccount account, string refreshToken, string? accessToken)
        {
            Interlocked.Increment(ref Saves);
            Slots[account.CredentialKey] = (refreshToken, accessToken);
        }
    }

    /// <summary>
    /// A fake Silo per access token: <see cref="Profiles"/> answers the profile list, a token missing there gets 401,
    /// and a refresh succeeds only for a refresh token in <see cref="Refreshes"/>.
    /// </summary>
    private sealed class Server : HttpMessageHandler
    {
        public readonly ConcurrentDictionary<string, int> Profiles = new();
        public readonly ConcurrentDictionary<string, string> Refreshes = new();
        public readonly ConcurrentQueue<(string Host, string Path, string? Bearer)> Requests = new();
        public HashSet<string> Unreachable { get; } = [];
        public TaskCompletionSource? Gate;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var bearer = request.Headers.Authorization?.Parameter;
            Requests.Enqueue((request.RequestUri!.Host, request.RequestUri.AbsolutePath, bearer));
            if (Gate is { } gate) await gate.Task.WaitAsync(cancellationToken);
            if (Unreachable.Contains(request.RequestUri.Host)) throw new HttpRequestException("No route to host.");
            if (request.RequestUri.AbsolutePath.EndsWith("/auth/refresh", StringComparison.Ordinal))
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                var token = JsonDocument.Parse(body).RootElement.GetProperty("refresh_token").GetString()!;
                return Refreshes.TryGetValue(token, out var access)
                    ? Json(HttpStatusCode.OK, $$"""{"access_token":"{{access}}","refresh_token":"{{token}}-next","expires_in":3600}""", request)
                    : Json(HttpStatusCode.Unauthorized, Unauthorized, request);
            }
            if (bearer is not null && Profiles.TryGetValue(bearer, out var count))
            {
                var items = string.Join(",", Enumerable.Range(0, count).Select(i => $$"""{"id":"p{{i}}"}"""));
                return Json(HttpStatusCode.OK, $$"""{"items":[{{items}}]}""", request);
            }
            return Json(HttpStatusCode.Unauthorized, Unauthorized, request);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string json, HttpRequestMessage request) => new(status)
        {
            RequestMessage = request,
            Content = new StringContent(json, Encoding.UTF8, (int)status >= 400 ? "application/problem+json" : "application/json"),
        };
    }

    private sealed record Fixture(SavedServerRegistry Registry, Vault Vault, Server Server, SavedAccount Alex, SavedAccount Sam,
        SavedAccount Robin, SavedAccount Kim)
    {
        public AccountProfilesRefresher Refresher() => new(Registry, AccountProfilesRefresher.FromSavedSessions(Registry, Vault,
            url => new SiloClient(url, new ClientIdentity("d", "Test Xbox", "xbox", "Bale"), Server)));

        public SavedAccount Get(SavedAccount account) => Registry.Snapshot.Accounts.Single(a => a.Id == account.Id);
    }

    /// <summary>
    /// Home has alex (open) and sam; robin there must sign in again. Cabin has kim. Every signed-in account has a
    /// valid access token unless a test expires it.
    /// </summary>
    private static Fixture Create()
    {
        var registry = new SavedServerRegistry(new Storage());
        var vault = new Vault();
        var later = DateTimeOffset.UtcNow.AddHours(1);
        SavedAccount SignIn(string id, string name)
        {
            var account = registry.AdoptSignIn(id, name);
            registry.UpdateAccount(account.Id, a => a with { AccessTokenExpiresAt = later });
            vault.Slots[account.CredentialKey] = ($"rt-{name}", $"at-{name}");
            return registry.Snapshot.Accounts.Single(a => a.Id == account.Id);
        }
        registry.AddAndActivate(new Uri("https://cabin.example.com"), "Cabin");
        var kim = SignIn("u-kim", "kim");
        registry.AddAndActivate(new Uri("https://home.example.com"), "Home");
        registry.ActivateAccount(registry.Active!.Id, null);
        var sam = SignIn("u-sam", "sam");
        registry.ActivateAccount(registry.Active.Id, null);
        var robin = SignIn("u-robin", "robin");
        registry.MarkNeedsSignIn(robin.Id);
        registry.ActivateAccount(registry.Active.Id, null);
        var alex = SignIn("u-alex", "alex");
        registry.RecordProfileCount(alex.Id, 1);
        registry.RecordProfileCount(sam.Id, 2);
        registry.RecordProfileCount(kim.Id, 4);
        var server = new Server();
        server.Profiles["at-alex"] = 3;
        server.Profiles["at-sam"] = 2;
        server.Profiles["at-kim"] = 5;
        return new Fixture(registry, vault, server, alex, sam, registry.Snapshot.Accounts.Single(a => a.Id == robin.Id), kim);
    }

    [Fact]
    public async Task RefreshesEverySignedInAccountWithItsOwnTokensAndSkipsSignInAgain()
    {
        var f = Create();
        Assert.Equal(f.Alex.Id, f.Registry.ActiveAccount!.Id);

        var changed = await f.Refresher().RefreshAllAsync();

        // sam's count didn't change, so only alex and kim are reported.
        Assert.Equal(new[] { f.Alex.Id, f.Kim.Id }.Order(), changed.Order());
        Assert.Equal(3, f.Get(f.Alex).ProfileCount);
        Assert.Equal(2, f.Get(f.Sam).ProfileCount);
        Assert.Equal(5, f.Get(f.Kim).ProfileCount);
        Assert.Null(f.Get(f.Robin).ProfileCount);
        // One profile list per signed-in account, each with that account's own bearer on its own server.
        var requests = f.Server.Requests.ToArray();
        Assert.Equal(3, requests.Length);
        Assert.All(requests, r => Assert.Equal("/api/v2/profiles", r.Path));
        Assert.Contains(("home.example.com", "/api/v2/profiles", "at-alex"), requests);
        Assert.Contains(("home.example.com", "/api/v2/profiles", "at-sam"), requests);
        Assert.Contains(("cabin.example.com", "/api/v2/profiles", "at-kim"), requests);
        Assert.DoesNotContain(requests, r => r.Bearer == "at-robin");
        // Nothing about the selection or the vault changed.
        Assert.Equal(f.Alex.Id, f.Registry.ActiveAccount!.Id);
        Assert.Equal(0, f.Vault.Saves);
    }

    [Fact]
    public async Task UnreachableServersAndRefusedSessionsKeepTheirCountAndAreNeverSignedOut()
    {
        var f = Create();
        f.Server.Unreachable.Add("cabin.example.com");
        f.Server.Profiles.TryRemove("at-sam", out _); // sam's access token is refused and so is the refresh.
        var before = f.Registry.Snapshot;
        var slots = f.Vault.Slots.ToArray();

        var changed = await f.Refresher().RefreshAllAsync();

        Assert.Equal([f.Alex.Id], changed);
        Assert.Equal(4, f.Get(f.Kim).ProfileCount);
        Assert.Equal(2, f.Get(f.Sam).ProfileCount);
        Assert.False(f.Get(f.Sam).NeedsSignIn);
        Assert.False(f.Get(f.Kim).NeedsSignIn);
        Assert.Equal(before.Entries, f.Registry.Snapshot.Entries);
        Assert.Equal(before.ActiveAccountId, f.Registry.Snapshot.ActiveAccountId);
        Assert.Equal(slots.OrderBy(s => s.Key), f.Vault.Slots.ToArray().OrderBy(s => s.Key));
        Assert.Contains(f.Server.Requests, r => r.Path.EndsWith("/auth/refresh", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnExpiredAccessTokenRefreshesIntoThatAccountsOwnSlot()
    {
        var f = Create();
        f.Registry.UpdateAccount(f.Sam.Id, a => a with { AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5) });
        f.Server.Profiles.TryRemove("at-sam", out _);
        f.Server.Refreshes["rt-sam"] = "at-sam-2";
        f.Server.Profiles["at-sam-2"] = 6;

        await f.Refresher().RefreshAllAsync();

        Assert.Equal(6, f.Get(f.Sam).ProfileCount);
        Assert.Equal(("rt-sam-next", "at-sam-2"), f.Vault.Slots[f.Sam.CredentialKey]);
        Assert.True(f.Get(f.Sam).AccessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(30));
        Assert.Equal(1, f.Vault.Saves);
        Assert.Equal(("rt-alex", "at-alex"), f.Vault.Slots[f.Alex.CredentialKey]);
    }

    [Fact]
    public async Task TheOpenAccountsSlotBelongsToTheLiveSession()
    {
        var f = Create();
        f.Registry.UpdateAccount(f.Alex.Id, a => a with { AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5) });
        f.Server.Profiles.TryRemove("at-alex", out _);
        f.Server.Refreshes["rt-alex"] = "at-alex-2";
        f.Server.Profiles["at-alex-2"] = 7;

        await f.Refresher().RefreshAllAsync();

        Assert.Equal(7, f.Get(f.Alex).ProfileCount);
        Assert.Equal(("rt-alex", "at-alex"), f.Vault.Slots[f.Alex.CredentialKey]);
        Assert.Equal(0, f.Vault.Saves);
    }

    [Fact]
    public async Task RotatedTokensNeverOverwriteASlotThatChangedOrAnAccountThatLeft()
    {
        var f = Create();
        foreach (var account in new[] { f.Sam, f.Kim })
        {
            f.Registry.UpdateAccount(account.Id, a => a with { AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5) });
            f.Server.Profiles.TryRemove($"at-{account.Username}", out _);
            f.Server.Refreshes[$"rt-{account.Username}"] = $"at-{account.Username}-2";
            f.Server.Profiles[$"at-{account.Username}-2"] = 1;
        }
        f.Server.Gate = new TaskCompletionSource();
        var refresh = f.Refresher().RefreshAllAsync();
        // While the reads wait: sam signs in again elsewhere (a newer pair) and kim signs out.
        f.Vault.Slots[f.Sam.CredentialKey] = ("rt-sam-newer", "at-sam-newer");
        f.Registry.RemoveAccount(f.Kim.Id);
        f.Vault.Slots.TryRemove(f.Kim.CredentialKey, out _);
        f.Server.Gate.SetResult();
        await refresh;

        Assert.Equal(("rt-sam-newer", "at-sam-newer"), f.Vault.Slots[f.Sam.CredentialKey]);
        Assert.False(f.Vault.Slots.ContainsKey(f.Kim.CredentialKey));
        Assert.DoesNotContain(f.Registry.Snapshot.Accounts, a => a.Id == f.Kim.Id);
        Assert.Equal(0, f.Vault.Saves);
    }

    [Fact]
    public async Task UnapprovedHttpServersAndAccountsWithoutASavedSessionSendNothing()
    {
        var registry = new SavedServerRegistry(new Storage());
        var vault = new Vault();
        registry.AddAndActivate(new Uri("http://media.example.com:8090"));
        var http = registry.AdoptSignIn("u1", "alex");
        registry.UpdateAccount(http.Id, a => a with { AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        vault.Slots[http.CredentialKey] = ("rt", "at");
        registry.AddAndActivate(new Uri("https://example.com"));
        registry.AdoptSignIn("u2", "sam"); // no vault slot
        var server = new Server();
        server.Profiles["at"] = 2;
        var refresher = new AccountProfilesRefresher(registry, AccountProfilesRefresher.FromSavedSessions(registry, vault,
            url => new SiloClient(url, new ClientIdentity("d", "Test Xbox", "xbox", "Bale"), server)));

        Assert.Empty(await refresher.RefreshAllAsync());
        Assert.Empty(server.Requests);

        registry.ApproveHttp(new Uri("http://media.example.com:8090"));
        Assert.Equal([http.Id], await refresher.RefreshAllAsync());
        Assert.Equal(2, registry.Snapshot.Accounts.Single(a => a.Id == http.Id).ProfileCount);
    }

    [Fact]
    public async Task ACancelledRefreshRecordsNothing()
    {
        var f = Create();
        f.Server.Gate = new TaskCompletionSource();
        using var cancel = new CancellationTokenSource();
        var refresh = f.Refresher().RefreshAllAsync(cancel.Token);
        cancel.Cancel();
        Assert.Empty(await refresh);
        Assert.Equal(1, f.Get(f.Alex).ProfileCount);
    }

    [Fact]
    public async Task ThePreviewFixtureRefreshesWithoutTheNetwork()
    {
        var preview = new ConnectionPreview();
        var alex = preview.Servers.Snapshot.Accounts.Single(a => a.Username == "alex");
        var sam = preview.Servers.Snapshot.Accounts.Single(a => a.Username == "sam");
        var robin = preview.Servers.Snapshot.Accounts.Single(a => a.Username == "robin");
        Assert.Equal(1, alex.ProfileCount);
        Assert.Null(sam.ProfileCount);

        var changed = await new AccountProfilesRefresher(preview.Servers, preview.ProfileCounts).RefreshAllAsync();

        Assert.Equal(2, changed.Count);
        var state = preview.Servers.Snapshot.Accounts;
        Assert.Equal(2, state.Single(a => a.Id == alex.Id).ProfileCount);
        Assert.Equal(3, state.Single(a => a.Id == sam.Id).ProfileCount);
        Assert.Null(state.Single(a => a.Id == robin.Id).ProfileCount);
        Assert.True(state.Single(a => a.Id == robin.Id).NeedsSignIn);
    }

    [Fact]
    public void RecordProfileCountWritesOnlyChangesOfSavedAccounts()
    {
        var storage = new Storage();
        var registry = new SavedServerRegistry(storage);
        registry.AddAndActivate(new Uri("https://example.com"));
        var account = registry.AdoptSignIn("u1", "alex");
        registry.ActivateAccount(registry.Active!.Id, null);
        var json = storage.Json;

        Assert.False(registry.RecordProfileCount("missing", 2));
        Assert.Equal(json, storage.Json);
        Assert.True(registry.RecordProfileCount(account.Id, 2));
        Assert.False(registry.RecordProfileCount(account.Id, 2));
        Assert.Equal(2, new SavedServerRegistry(storage).Snapshot.Accounts.Single().ProfileCount);
        // Recording a count for an account that isn't open never opens it.
        Assert.Null(registry.ActiveAccount);
    }

    [Fact]
    public void UpdateAccountNeverChangesIdentityOrVaultSlot()
    {
        var registry = new SavedServerRegistry(new Storage());
        registry.AddAndActivate(new Uri("https://example.com"));
        var account = registry.AdoptSignIn("u1", "alex");
        Assert.True(registry.UpdateAccount(account.Id, a => a with { Id = "x", CredentialKey = "stolen", ServerId = "y", ProfileCount = 4 }));
        Assert.Equal(account with { ProfileCount = 4 }, registry.Snapshot.Accounts.Single());
        Assert.False(registry.UpdateAccount("missing", a => a));
    }
}
