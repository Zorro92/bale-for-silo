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
        var registry = new SavedServerRegistry(storage, legacy);
        var migrated = Assert.Single(registry.Snapshot.Entries);
        Assert.Equal(migrated.Id, registry.Snapshot.ActiveServerId);
        Assert.Equal(oldKey, migrated.CredentialKey);
        Assert.Equal("https://example.com/silo", migrated.Url);
        Assert.Equal(expiry, migrated.AccessTokenExpiresAt);
        Assert.Equal(legacy.ProfileId, migrated.ProfileId);
        Assert.Equal(legacy.ProfileName, migrated.ProfileName);
        Assert.Equal(legacy.ProfileAvatar, migrated.ProfileAvatar);
        Assert.Equal(legacy.AccountName, migrated.AccountName);
        Assert.Equal("Example", migrated.DisplayName);
        Assert.DoesNotContain(refresh, storage.Json!);
        Assert.DoesNotContain(access, storage.Json!);
        Assert.DoesNotContain(proof, storage.Json!);

        // This is the launch path's actual credential adoption, with no transport call or re-login.
        using var client = new SiloClient(new Uri(migrated.Url), new ClientIdentity("test", "Test", "test", "Bale"));
        var credentials = vault[migrated.CredentialKey];
        client.RestoreSession(credentials.Refresh, credentials.Access, migrated.AccessTokenExpiresAt, migrated.ProfileId, credentials.Proof);
        Assert.True(client.IsSignedIn);
        Assert.Equal(refresh, client.RefreshToken);
        Assert.Equal(access, client.AccessToken);
        Assert.Equal(proof, client.ProfileToken);
        Assert.Equal("profile", client.ProfileId);

        var reloaded = new SavedServerRegistry(storage, new LegacyServerSession("https://other.example.com"));
        Assert.Equal(migrated, reloaded.Active);
        Assert.Equal(1, storage.Writes);
    }

    [Fact]
    public void LegacyRefreshOnlyAndHttpSessionsKeepTheirCredentialsWhileConsentRemainsRequired()
    {
        var registry = new SavedServerRegistry(new Storage(), new LegacyServerSession("http://192.0.2.10:8090"));
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
    public void UpsertAndSwitchPreservePerServerProfileAndVaultScope()
    {
        var storage = new Storage();
        var registry = new SavedServerRegistry(storage);
        var first = new Uri("https://example.com");
        var second = new Uri("https://second.example.com");
        registry.AddAndActivate(first, "First");
        registry.UpdateActive(s => s with { ProfileId = "first-profile", AccountName = "First viewer" });
        registry.AddAndActivate(second, "Second");
        Assert.Null(registry.Active!.ProfileId);
        registry.UpdateActive(s => s with { ProfileId = "second-profile" });
        registry.AddAndActivate(new Uri("https://EXAMPLE.com/"));
        Assert.Equal(2, registry.Snapshot.Entries.Length);
        Assert.Equal("first-profile", registry.Active!.ProfileId);
        Assert.Equal("First", registry.Active.FetchedName);
        registry.Activate(SavedServerRegistry.IdFor(second));
        var reloaded = new SavedServerRegistry(storage);
        Assert.Equal("second-profile", reloaded.Active!.ProfileId);
        Assert.Equal(second.ToString().TrimEnd('/'), reloaded.Active.CredentialKey);
        Assert.Equal(reloaded.Active.Id, reloaded.Sorted[0].Id);
    }

    [Fact]
    public void RemovePromotesMostRecentAndEmptyListDoesNotRemigrate()
    {
        var storage = new Storage();
        var registry = new SavedServerRegistry(storage);
        registry.AddAndActivate(new Uri("https://example.com"));
        var first = registry.Active!.Id;
        registry.AddAndActivate(new Uri("https://second.example.com"));
        var second = registry.Active!.Id;
        registry.Remove(first);
        Assert.Equal(second, registry.Active!.Id);
        registry.Remove(second);
        Assert.Null(registry.Active);
        Assert.Empty(new SavedServerRegistry(storage, new LegacyServerSession("https://example.com")).Snapshot.Entries);
    }

    [Fact]
    public void FailedWritesDoNotPublishNewStateAndCorruptStateIsNeverOverwritten()
    {
        var storage = new Storage();
        var registry = new SavedServerRegistry(storage, new LegacyServerSession("https://example.com"));
        var before = storage.Json;
        storage.FailWrites = true;
        Assert.Throws<IOException>(() => registry.Remove(registry.Active!.Id));
        Assert.NotNull(registry.Active);
        Assert.Equal(before, storage.Json);
        storage.FailWrites = false;
        storage.Json = "{";
        Assert.Throws<JsonException>(() => new SavedServerRegistry(storage, new LegacyServerSession("https://example.com")));
        Assert.Equal("{", storage.Json);
        Assert.Equal(1, storage.Writes);
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
}
