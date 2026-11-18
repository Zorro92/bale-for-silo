using System.Text.Json;
using Bale.Core.Connection;

namespace Bale.Core.Tests;

public class SavedServerRegistryTests
{
    private sealed class Storage : ISavedServerStorage
    {
        public string? Json;
        public bool FailWrites;
        public int Writes;
        public string? Read() => Json;
        public void Write(string json)
        {
            if (FailWrites) throw new IOException("Storage is full.");
            Json = json;
            Writes++;
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static SavedServerMigration Legacy(string? url, Func<string, bool>? signedIn = null, LegacyServerSession? session = null) =>
        new(null, session ?? new LegacyServerSession(url), signedIn ?? (_ => true));

    private static string V1(object state) => JsonSerializer.Serialize(state);

    [Fact]
    public void MigratesLegacyMetadataWithoutTouchingVaultKeysOrRequiringLogin()
    {
        const string oldKey = "https://EXAMPLE.com:443/silo";
        var refresh = Guid.NewGuid().ToString();
        var access = Guid.NewGuid().ToString();
        var proof = Guid.NewGuid().ToString();
        var vault = new Dictionary<string, (string Refresh, string Access, string Proof)> { [oldKey] = (refresh, access, proof) };
        var expiry = DateTimeOffset.UtcNow.AddHours(1);
        var storage = new Storage();
        var legacy = new LegacyServerSession(oldKey, "Example", "profile", "Viewer", "https://example.com/avatar.png", "Viewer", expiry);
        var registry = new SavedServerRegistry(storage, Legacy(null, vault.ContainsKey, legacy));
        var migrated = Assert.Single(registry.Snapshot.Entries);
        var account = Assert.Single(registry.Snapshot.Accounts);
        Assert.Equal(migrated.Id, registry.Snapshot.ActiveServerId);
        Assert.Equal(account, registry.ActiveAccount);
        Assert.Equal(oldKey, account.CredentialKey);
        Assert.True(account.AdoptsLegacyData);
        Assert.Equal(migrated.Id, account.ServerScope);
        Assert.Equal("profile", account.ProfileScope("profile"));
        Assert.Equal("https://example.com/silo", migrated.Url);
        Assert.Equal(expiry, account.AccessTokenExpiresAt);
        Assert.Equal(legacy.ProfileId, account.ProfileId);
        Assert.Equal(legacy.ProfileName, account.ProfileName);
        Assert.Equal(legacy.ProfileAvatar, account.ProfileAvatar);
        Assert.Equal(legacy.AccountName, account.Username);
        Assert.Equal("Example", migrated.DisplayName);
        Assert.DoesNotContain(refresh, storage.Json!);
        Assert.DoesNotContain(access, storage.Json!);
        Assert.DoesNotContain(proof, storage.Json!);

        // This is the launch path's actual credential adoption, with no transport call or re-login.
        using var client = new SiloClient(new Uri(migrated.Url), new ClientIdentity("test", "Test", "test", "Bale"));
        var credentials = vault[account.CredentialKey];
        client.RestoreSession(credentials.Refresh, credentials.Access, account.AccessTokenExpiresAt, account.ProfileId, credentials.Proof);
        Assert.True(client.IsSignedIn);
        Assert.Equal(refresh, client.RefreshToken);
        Assert.Equal(access, client.AccessToken);
        Assert.Equal(proof, client.ProfileToken);
        Assert.Equal("profile", client.ProfileId);

        var reloaded = new SavedServerRegistry(storage, Legacy("https://other.example.com"));
        Assert.Equal(migrated, reloaded.Active);
        Assert.Equal(account, reloaded.ActiveAccount);
        Assert.Equal(1, storage.Writes);
        Assert.Equal(AccountRoute.Restore, reloaded.Route(selectionRequired: true, _ => true));
    }

    [Fact]
    public void MigratesEveryOneAccountServerKeepingItsVaultSlotAndSelection()
    {
        var first = new Uri("https://example.com");
        var second = new Uri("https://second.example.com");
        var signedOut = new Uri("https://third.example.com");
        string Id(Uri uri) => SavedServerRegistry.IdFor(uri);
        var v1 = V1(new
        {
            Entries = new object[]
            {
                new { Id = Id(first), Url = "https://example.com", CredentialKey = "https://EXAMPLE.com/", FetchedName = "Home", ProfileId = "default", AccountName = "alex", LastUsedAtEpochMs = 10L },
                new { Id = Id(second), Url = "https://second.example.com", CredentialKey = "https://second.example.com", ProfileId = "default", AccountName = "sam", LastUsedAtEpochMs = 20L, VerifiedServerId = "srv-2" },
                new { Id = Id(signedOut), Url = "https://third.example.com", CredentialKey = "https://third.example.com", LastUsedAtEpochMs = 5L },
            },
            ActiveServerId = Id(second),
            ApprovedHttpOrigins = new[] { "digest" },
        });
        var storage = new Storage();
        var registry = new SavedServerRegistry(storage, new SavedServerMigration(v1, new LegacyServerSession("https://ignored.example.com"),
            key => key != "https://third.example.com"));
        var state = registry.Snapshot;
        Assert.Equal(3, state.Entries.Length);
        Assert.Equal(2, state.Accounts.Length);
        Assert.Equal(["digest"], state.ApprovedHttpOrigins);
        Assert.Equal("sam", registry.ActiveAccount!.Username);
        Assert.Equal("https://second.example.com", registry.ActiveAccount.CredentialKey);
        Assert.Equal("srv-2", registry.Active!.VerifiedServerId);
        Assert.Equal("https://EXAMPLE.com/", registry.AccountsOn(Id(first)).Single().CredentialKey);
        Assert.All(state.Accounts, account => Assert.True(account.AdoptsLegacyData));
        Assert.True(state.Entries.Single(s => s.Id == Id(signedOut)).LegacyDataUnclaimed);
        Assert.Empty(registry.AccountsOn(Id(signedOut)));

        // The signed-out server from before accounts is removed at launch; the active one never is.
        Assert.Equal(1, registry.RemoveAccountlessServers());
        Assert.Null(registry.Find(signedOut));
        Assert.Equal(0, registry.RemoveAccountlessServers());
    }

    [Fact]
    public void UnreadableV1ListIsNeverMigratedOrOverwritten()
    {
        var storage = new Storage();
        Assert.Throws<JsonException>(() => new SavedServerRegistry(storage, new SavedServerMigration("{", null, _ => true)));
        Assert.Null(storage.Json);
        Assert.Equal(0, storage.Writes);
    }

    [Fact]
    public void LegacyRefreshOnlyAndHttpSessionsKeepTheirCredentialsWhileConsentRemainsRequired()
    {
        var registry = new SavedServerRegistry(new Storage(), Legacy("http://192.0.2.10:8090"));
        var entry = Assert.Single(registry.Snapshot.Entries);
        var refresh = Guid.NewGuid().ToString();
        using var client = new SiloClient(new Uri(entry.Url), new ClientIdentity("test", "Test", "test", "Bale"));
        client.RestoreSession(refresh);
        Assert.True(client.IsSignedIn);
        Assert.Equal(refresh, client.RefreshToken);
        Assert.True(registry.RequiresHttpApproval(client.Server));
        registry.ApproveHttp(client.Server);
        Assert.False(registry.RequiresHttpApproval(client.Server));
        Assert.Equal(refresh, client.RefreshToken);
    }

    [Fact]
    public void SignInsAddAccountsPerServerAndRefreshDuplicatesInsteadOfAddingThem()
    {
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1000));
        var storage = new Storage();
        var registry = new SavedServerRegistry(storage, time: time);
        var home = new Uri("https://example.com");
        registry.AddAndActivate(home, "Home");
        Assert.Null(registry.ActiveAccount);
        var alex = registry.AdoptSignIn("u-alex", "alex");
        Assert.False(alex.AdoptsLegacyData);
        Assert.NotEqual(alex.ServerId, alex.ServerScope);
        Assert.Equal($"{alex.Id}/default", alex.ProfileScope("default"));
        registry.UpdateActiveAccount(a => a with { ProfileId = "default", ProfileCount = 2 });

        // "Add an account" opens the server with no account; a different user becomes a second account.
        time.Advance(TimeSpan.FromSeconds(1));
        registry.ActivateAccount(alex.ServerId, null);
        var sam = registry.AdoptSignIn("u-sam", "sam");
        Assert.NotEqual(alex.Id, sam.Id);
        Assert.NotEqual(alex.CredentialKey, sam.CredentialKey);
        Assert.Null(sam.ProfileId);
        Assert.Equal([alex.Id, sam.Id], registry.AccountsOn(alex.ServerId).Select(a => a.Id));

        // Signing in again as a saved account refreshes it, keeping its profile and vault slot.
        registry.ActivateAccount(alex.ServerId, null);
        var again = registry.AdoptSignIn("u-alex", "Alex");
        Assert.Equal(alex.Id, again.Id);
        Assert.Equal(alex.CredentialKey, again.CredentialKey);
        Assert.Equal("default", again.ProfileId);
        Assert.Equal(2, registry.Snapshot.Accounts.Length);

        // The open account signing in again with no known identity is refreshed, not duplicated.
        registry.MarkNeedsSignIn(again.Id);
        Assert.True(registry.ActiveAccount!.NeedsSignIn);
        var refreshed = registry.AdoptSignIn(null, null);
        Assert.Equal(alex.Id, refreshed.Id);
        Assert.False(refreshed.NeedsSignIn);

        // ...but a provably different user signing in there is saved as another account.
        registry.MarkNeedsSignIn(alex.Id);
        var robin = registry.AdoptSignIn("u-robin", "robin");
        Assert.NotEqual(alex.Id, robin.Id);
        Assert.True(registry.AccountsOn(alex.ServerId).Single(a => a.Id == alex.Id).NeedsSignIn);
        Assert.Equal(3, new SavedServerRegistry(storage).Snapshot.Accounts.Length);
    }

    [Fact]
    public void UpsertAndSwitchKeepEachAccountsProfileAndVaultScope()
    {
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1000));
        var storage = new Storage();
        var registry = new SavedServerRegistry(storage, time: time);
        var first = new Uri("https://example.com");
        var second = new Uri("https://second.example.com");
        registry.AddAndActivate(first, "First");
        registry.AdoptSignIn("1", "First viewer");
        registry.UpdateActiveAccount(a => a with { ProfileId = "first-profile" });
        time.Advance(TimeSpan.FromSeconds(1));
        registry.AddAndActivate(second, "Second");
        Assert.Null(registry.ActiveAccount);
        registry.AdoptSignIn("1", "Second viewer");
        registry.UpdateActiveAccount(a => a with { ProfileId = "second-profile" });
        time.Advance(TimeSpan.FromSeconds(1));
        registry.AddAndActivate(new Uri("https://EXAMPLE.com/"));
        Assert.Equal(2, registry.Snapshot.Entries.Length);
        Assert.Equal("first-profile", registry.ActiveAccount!.ProfileId);
        Assert.Equal("First", registry.Active!.FetchedName);
        time.Advance(TimeSpan.FromSeconds(1));
        registry.Activate(SavedServerRegistry.IdFor(second));
        var reloaded = new SavedServerRegistry(storage);
        Assert.Equal("second-profile", reloaded.ActiveAccount!.ProfileId);
        Assert.StartsWith("https://second.example.com#", reloaded.ActiveAccount.CredentialKey, StringComparison.Ordinal);
        Assert.Equal(reloaded.Active!.Id, reloaded.Sorted[0].Id);
        Assert.Equal(reloaded.Active.Id, reloaded.Groups[0].Server.Id);
    }

    [Fact]
    public void UpdatesNeverChangeIdentityOrVaultSlot()
    {
        var registry = new SavedServerRegistry(new Storage());
        registry.AddAndActivate(new Uri("https://example.com"));
        var account = registry.AdoptSignIn("1", "alex");
        registry.UpdateActiveAccount(a => a with { Id = "other", CredentialKey = "stolen", ServerId = "x", AdoptsLegacyData = true, Username = "renamed" });
        registry.UpdateActive(s => s with { Id = "other", Url = "https://evil.example.com", FetchedName = "Renamed" });
        Assert.Equal(account with { Username = "renamed" }, registry.ActiveAccount);
        Assert.Equal("https://example.com", registry.Active!.Url);
        Assert.Equal("Renamed", registry.Active.DisplayName);
    }

    [Fact]
    public void SigningOutOfOneAccountLeavesTheOthersAndTheServer()
    {
        var registry = new SavedServerRegistry(new Storage());
        var home = new Uri("https://example.com");
        registry.AddAndActivate(home);
        var alex = registry.AdoptSignIn("1", "alex");
        registry.ActivateAccount(alex.ServerId, null);
        var sam = registry.AdoptSignIn("2", "sam");

        var removed = registry.RemoveAccount(sam.Id);
        Assert.Equal([sam], removed);
        Assert.Null(registry.ActiveAccount);
        Assert.Equal(alex.ServerId, registry.Active!.Id);
        Assert.Equal([alex.Id], registry.AccountsOn(alex.ServerId).Select(a => a.Id));
        // Another account still has a session: Change Account is the root, not sign-in.
        Assert.Equal(AccountRoute.ChangeAccount, registry.Route(selectionRequired: false, _ => true));
        Assert.Equal(AccountRoute.SignIn, registry.Route(selectionRequired: false, _ => false));

        // Signing out of a non-open account keeps the open one.
        registry.ActivateAccount(alex.ServerId, alex.Id);
        registry.ActivateAccount(alex.ServerId, null);
        var robin = registry.AdoptSignIn("3", "robin");
        registry.RemoveAccount(alex.Id);
        Assert.Equal(robin.Id, registry.ActiveAccount!.Id);

        // The last account's Sign Out & Remove Server forgets the server too.
        Assert.Equal([robin], registry.RemoveAccount(robin.Id, removeServer: true));
        Assert.Empty(registry.Snapshot.Entries);
        Assert.Equal(AccountRoute.ServerEntry, registry.Route(selectionRequired: false, _ => true));
    }

    [Fact]
    public void RemovePromotesMostRecentServerAndItsAccountAndEmptyListDoesNotRemigrate()
    {
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1000));
        var storage = new Storage();
        var registry = new SavedServerRegistry(storage, time: time);
        registry.AddAndActivate(new Uri("https://example.com"));
        var first = registry.Active!.Id;
        var alex = registry.AdoptSignIn("1", "alex");
        registry.ActivateAccount(first, null);
        registry.AdoptSignIn("2", "sam");
        time.Advance(TimeSpan.FromSeconds(1));
        registry.ActivateAccount(first, alex.Id);
        time.Advance(TimeSpan.FromSeconds(1));
        registry.AddAndActivate(new Uri("https://second.example.com"));
        var second = registry.Active!.Id;
        registry.AdoptSignIn("1", "alex");
        var removed = registry.Remove(second);
        Assert.Single(removed);
        Assert.Equal(first, registry.Active!.Id);
        Assert.Equal(alex.Id, registry.ActiveAccount!.Id);
        Assert.Equal(2, registry.Remove(first).Count);
        Assert.Null(registry.Active);
        Assert.Null(registry.ActiveAccount);
        Assert.Empty(new SavedServerRegistry(storage, Legacy("https://example.com")).Snapshot.Entries);
    }

    [Fact]
    public void LaunchRouteAsksWhoIsWatchingThroughChangeAccountOnlyWithSeveralAccounts()
    {
        var registry = new SavedServerRegistry(new Storage());
        Assert.Equal(AccountRoute.ServerEntry, registry.Route(selectionRequired: true, _ => true));
        registry.AddAndActivate(new Uri("https://example.com"));
        Assert.Equal(AccountRoute.SignIn, registry.Route(selectionRequired: true, _ => true));
        var alex = registry.AdoptSignIn("1", "alex");
        Assert.Equal(AccountRoute.Restore, registry.Route(selectionRequired: true, _ => true));
        registry.AddAndActivate(new Uri("https://second.example.com"));
        registry.AdoptSignIn("1", "alex");
        Assert.Equal(AccountRoute.ChangeAccount, registry.Route(selectionRequired: true, _ => true));
        Assert.Equal(AccountRoute.Restore, registry.Route(selectionRequired: false, _ => true));
        // An account that must sign in again still counts, but opens sign-in when it is the only session left.
        registry.MarkNeedsSignIn(registry.ActiveAccount!.Id);
        Assert.Equal(AccountRoute.ChangeAccount, registry.Route(selectionRequired: false, _ => true));
        Assert.Equal(AccountRoute.SignIn, registry.Route(selectionRequired: false, account => account.Id != alex.Id));
    }

    [Fact]
    public void FirstAccountOnAServerWithUnclaimedLegacyDataAdoptsIt()
    {
        var registry = new SavedServerRegistry(new Storage(), Legacy("https://example.com", _ => false));
        Assert.Empty(registry.Snapshot.Accounts);
        Assert.True(registry.Active!.LegacyDataUnclaimed);
        var first = registry.AdoptSignIn("1", "alex");
        Assert.True(first.AdoptsLegacyData);
        Assert.False(registry.Active!.LegacyDataUnclaimed);
        registry.ActivateAccount(first.ServerId, null);
        Assert.False(registry.AdoptSignIn("2", "sam").AdoptsLegacyData);
    }

    [Fact]
    public void FailedWritesDoNotPublishNewStateAndCorruptStateIsNeverOverwritten()
    {
        var storage = new Storage();
        var registry = new SavedServerRegistry(storage, Legacy("https://example.com"));
        var before = storage.Json;
        storage.FailWrites = true;
        Assert.Throws<IOException>(() => registry.Remove(registry.Active!.Id));
        Assert.Throws<IOException>(() => registry.AdoptSignIn("1", "alex"));
        Assert.NotNull(registry.Active);
        Assert.Single(registry.Snapshot.Accounts);
        Assert.Equal(before, storage.Json);
        storage.FailWrites = false;
        storage.Json = "{";
        Assert.Throws<JsonException>(() => new SavedServerRegistry(storage, Legacy("https://example.com")));
        Assert.Equal("{", storage.Json);
        Assert.Equal(1, storage.Writes);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("url")]
    [InlineData("missing-active")]
    [InlineData("null-active")]
    [InlineData("empty-active")]
    [InlineData("orphan-account")]
    [InlineData("duplicate-slot")]
    [InlineData("foreign-open-account")]
    [InlineData("missing-accounts")]
    public void InconsistentRegistryIsNeverRemigratedOrOverwritten(string inconsistency)
    {
        var storage = new Storage();
        var registry = new SavedServerRegistry(storage, Legacy("https://example.com"));
        registry.AddAndActivate(new Uri("https://second.example.com"));
        registry.AdoptSignIn("2", "sam");
        var writes = storage.Writes;
        var state = registry.Snapshot;
        var account = state.Accounts[0];
        object broken = inconsistency switch
        {
            "id" => state with { Entries = [state.Entries[0] with { Id = "wrong" }, state.Entries[1]] },
            "url" => state with { Entries = [state.Entries[0] with { Url = "https://EXAMPLE.com:443/" }, state.Entries[1]] },
            "missing-active" => state with { ActiveServerId = "missing" },
            "null-active" => state with { ActiveServerId = null },
            "empty-active" => state with { Entries = [], Accounts = [] },
            "orphan-account" => state with { Accounts = [account with { ServerId = "missing" }, state.Accounts[1]] },
            "duplicate-slot" => state with { Accounts = [account, state.Accounts[1] with { CredentialKey = account.CredentialKey }] },
            "foreign-open-account" => state with { ActiveAccountId = account.Id },
            _ => new { state.Entries, state.ActiveServerId, state.ApprovedHttpOrigins },
        };
        storage.Json = JsonSerializer.Serialize(broken);
        var before = storage.Json;
        Assert.Throws<JsonException>(() => new SavedServerRegistry(storage, Legacy("https://example.com")));
        Assert.Equal(before, storage.Json);
        Assert.Equal(writes, storage.Writes);
    }

    [Fact]
    public void HttpApprovalIsPersistedAsDigestAndScopedToExactOrigin()
    {
        var storage = new Storage();
        var registry = new SavedServerRegistry(storage);
        registry.ApproveHttp(new Uri("http://EXAMPLE.com:80/silo"));
        var reloaded = new SavedServerRegistry(storage);
        Assert.False(reloaded.RequiresHttpApproval(new Uri("http://example.com/other")));
        Assert.False(reloaded.RequiresHttpApproval(new Uri("https://example.com")));
        Assert.True(reloaded.RequiresHttpApproval(new Uri("http://example.com:8090")));
        Assert.True(reloaded.RequiresHttpApproval(new Uri("http://other.example.com")));
        Assert.DoesNotContain("example.com", storage.Json!);
    }
    [Fact]
    public void RenameOverridesTheServersNameAndBlankGoesBackToIt()
    {
        var storage = new Storage();
        var registry = new SavedServerRegistry(storage);
        registry.AddAndActivate(new Uri("https://example.com/silo"), "Example server");
        var id = registry.Active!.Id;
        Assert.Equal("example.com/silo", registry.Active.HostLabel);

        Assert.True(registry.Rename(id, "  Living room  "));
        Assert.Equal("Living room", registry.Active!.UserOverrideName);
        Assert.Equal("Living room", registry.Active.DisplayName);
        Assert.Equal("Example server", registry.Active.FetchedName);
        Assert.Equal("Living room", Assert.Single(registry.Groups).Server.DisplayName);

        // The name survives a reload, a new branding name and reconnecting to the same server.
        var reloaded = new SavedServerRegistry(storage);
        Assert.Equal("Living room", reloaded.Active!.DisplayName);
        reloaded.UpdateActive(s => s with { FetchedName = "Rebranded" });
        reloaded.AddAndActivate(new Uri("https://example.com/silo"), "Rebranded again");
        Assert.Equal("Living room", reloaded.Active!.DisplayName);

        Assert.True(reloaded.Rename(id, "   "));
        Assert.Null(reloaded.Active!.UserOverrideName);
        Assert.Equal("Rebranded again", reloaded.Active.DisplayName);
        Assert.True(reloaded.Rename(id, null));
        Assert.Equal("Rebranded again", reloaded.Active!.DisplayName);
        Assert.False(reloaded.Rename("missing", "Name"));
    }

    [Fact]
    public void RenameWritesNothingWhenTheServerIsGoneOrStorageFails()
    {
        var storage = new Storage();
        var registry = new SavedServerRegistry(storage);
        registry.AddAndActivate(new Uri("https://example.com"), "Home");
        var writes = storage.Writes;
        Assert.False(registry.Rename("missing", "Name"));
        Assert.Equal(writes, storage.Writes);

        storage.FailWrites = true;
        Assert.Throws<IOException>(() => registry.Rename(registry.Active!.Id, "Cabin"));
        Assert.Equal("Home", registry.Active!.DisplayName);
    }

    [Fact]
    public void ListsSavedBeforeRenamingLoadWithoutAName()
    {
        // A v2 blob written before UserOverrideName existed: no field, so the server's own name shows.
        var id = SavedServerRegistry.IdFor(new Uri("https://example.com"));
        var storage = new Storage
        {
            Json = $$"""{"Entries":[{"Id":"{{id}}","Url":"https://example.com","FetchedName":"Home","LastUsedAtEpochMs":1,"VerifiedServerId":null,"LegacyDataUnclaimed":false}],"Accounts":[],"ActiveServerId":"{{id}}","ActiveAccountId":null,"ApprovedHttpOrigins":[]}""",
        };
        var registry = new SavedServerRegistry(storage);
        Assert.Null(registry.Active!.UserOverrideName);
        Assert.Equal("Home", registry.Active.DisplayName);
        Assert.Equal("example.com", registry.Active.HostLabel);
    }

    [Fact]
    public void MigratedServersStartWithoutAName()
    {
        var first = new Uri("https://example.com");
        var v1 = V1(new
        {
            Entries = new object[] { new { Id = SavedServerRegistry.IdFor(first), Url = "https://example.com", CredentialKey = "https://example.com", FetchedName = "Home" } },
            ActiveServerId = SavedServerRegistry.IdFor(first),
            ApprovedHttpOrigins = Array.Empty<string>(),
        });
        var registry = new SavedServerRegistry(new Storage(), new SavedServerMigration(v1, null, _ => true));
        Assert.Null(registry.Active!.UserOverrideName);
        Assert.Equal("Home", registry.Active.DisplayName);
    }
}
