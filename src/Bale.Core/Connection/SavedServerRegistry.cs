using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bale.Core.Connection;

/// <summary>ServerEntry.kt: URL identity, branding and recency. Accounts and secrets live elsewhere.</summary>
/// <remarks>
/// <see cref="LegacyDataUnclaimed"/>: the server had no session when accounts were introduced, so the next
/// account to sign in here adopts its pre-account local data (ServerEntry.kt legacyDataUnclaimed).
/// </remarks>
public sealed record SavedServer(
    string Id, string Url, string? FetchedName = null, long LastUsedAtEpochMs = 0, string? VerifiedServerId = null,
    bool LegacyDataUnclaimed = false)
{
    public string DisplayName => string.IsNullOrWhiteSpace(FetchedName) ? new Uri(Url).Authority : FetchedName;
}

/// <summary>
/// AccountEntry.kt / silo-apple SavedSignIn: one saved login on one saved server. <see cref="Id"/> is a UUID made on
/// this device and never changes; <see cref="ServerUserId"/> only finds duplicates. Tokens stay in the platform vault
/// under <see cref="CredentialKey"/>.
/// </summary>
public sealed record SavedAccount(
    string Id, string ServerId, string CredentialKey, string? ServerUserId = null, string? Username = null,
    string? ProfileId = null, string? ProfileName = null, string? ProfileAvatar = null,
    DateTimeOffset? AccessTokenExpiresAt = null, int? ProfileCount = null, long AddedAtEpochMs = 0,
    long LastUsedAtEpochMs = 0, bool NeedsSignIn = false, bool AdoptsLegacyData = false)
{
    /// <summary>Management screens say "Account", never "User" (multi-account spec §2).</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Username) ? "Account" : Username;

    /// <summary>
    /// The key scope for this account's device-local data on its server. The account migrated from the
    /// one-account-per-server layout keeps the old keys, so nothing has to move (spec §5.2 step 2).
    /// </summary>
    public string ServerScope => AdoptsLegacyData ? ServerId : $"{ServerId}.{Id}";

    /// <summary>Profile ids repeat across accounts (spec §5.1), so profile-keyed data also carries the account.</summary>
    public string ProfileScope(string profileId) => AdoptsLegacyData ? profileId : $"{Id}/{profileId}";
}

public sealed record SavedServerState(
    SavedServer[] Entries, SavedAccount[] Accounts, string? ActiveServerId, string? ActiveAccountId, string[] ApprovedHttpOrigins);

/// <summary>The pre-v1 LocalSettings snapshot. Migration never reads, removes or re-keys credential-vault secrets.</summary>
public sealed record LegacyServerSession(string? ServerUrl, string? FetchedName = null, string? ProfileId = null,
    string? ProfileName = null, string? ProfileAvatar = null, string? AccountName = null, DateTimeOffset? AccessTokenExpiresAt = null);

/// <summary>
/// What a first launch of the account layout migrates from: the one-account-per-server list (<paramref name="V1Json"/>),
/// else the older LocalSettings session. <paramref name="HasCredentials"/> says whether a vault slot holds a session;
/// it must not need the network.
/// </summary>
public sealed record SavedServerMigration(string? V1Json, LegacyServerSession? Legacy, Func<string, bool> HasCredentials);

public interface ISavedServerStorage
{
    string? Read();
    void Write(string json);
}

/// <summary>Where the app goes at launch or after an account change (TvAppNavigation.kt start destination).</summary>
public enum AccountRoute
{
    /// <summary>No saved server: server setup.</summary>
    ServerEntry,
    /// <summary>Restore the active account's saved session.</summary>
    Restore,
    /// <summary>Sign in on the active server (no account open, or the open one must sign in again).</summary>
    SignIn,
    /// <summary>Change Account as the root screen.</summary>
    ChangeAccount,
}

/// <summary>One saved server and its accounts in the order Change Account shows them (AccountGroups.kt).</summary>
public sealed record AccountServerGroup(SavedServer Server, IReadOnlyList<SavedAccount> Accounts);

/// <summary>
/// AndroidServerRegistry.kt with the multi-account layer (silo-apple multi-account spec §5): one durable blob holding
/// the servers, their accounts and the selection. Publish state only after a successful write.
/// </summary>
public sealed class SavedServerRegistry
{
    private readonly ISavedServerStorage _storage;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private SavedServerState _state;

    public SavedServerRegistry(ISavedServerStorage storage, SavedServerMigration? migration = null, TimeProvider? time = null)
    {
        _storage = storage;
        _time = time ?? TimeProvider.System;
        if (storage.Read() is { } json)
        {
            // An unreadable blob is NOT an empty install: never remigrate or overwrite it.
            _state = Validate(JsonSerializer.Deserialize(json, SavedServerJson.Default.SavedServerState));
            return;
        }

        _state = migration is null ? Empty : Migrate(migration);
        // The written blob is also the migration marker: removing the last server must not resurrect old state.
        storage.Write(Serialize(_state));
    }

    private static SavedServerState Empty => new([], [], null, null, []);

    public SavedServerState Snapshot
    {
        get
        {
            lock (_gate) return _state with { Entries = [.. _state.Entries], Accounts = [.. _state.Accounts], ApprovedHttpOrigins = [.. _state.ApprovedHttpOrigins] };
        }
    }

    /// <summary>The active server.</summary>
    public SavedServer? Active { get { lock (_gate) return _state.Entries.FirstOrDefault(s => s.Id == _state.ActiveServerId); } }

    /// <summary>The open account on the active server; null while signing in there.</summary>
    public SavedAccount? ActiveAccount { get { lock (_gate) return _state.Accounts.FirstOrDefault(a => a.Id == _state.ActiveAccountId); } }

    /// <summary>Active server first, then by last use (TvServerListViewModel.kt / AccountGroups.kt).</summary>
    public IReadOnlyList<SavedServer> Sorted => Order(Snapshot);

    /// <summary>A server's accounts in the order they were added.</summary>
    public IReadOnlyList<SavedAccount> AccountsOn(string serverId)
    {
        lock (_gate) return [.. _state.Accounts.Where(a => a.ServerId == serverId).OrderBy(a => a.AddedAtEpochMs)];
    }

    /// <summary>buildAccountGroups: the Change Account and Settings › Accounts order.</summary>
    public IReadOnlyList<AccountServerGroup> Groups
    {
        get
        {
            var state = Snapshot;
            return [.. Order(state).Select(server => new AccountServerGroup(server,
                [.. state.Accounts.Where(a => a.ServerId == server.Id).OrderBy(a => a.AddedAtEpochMs)]))];
        }
    }

    public static string IdFor(Uri server) => Convert.ToBase64String(Encoding.UTF8.GetBytes(ServerAddress.ToBaseUrl(server)))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public SavedServer? Find(Uri server)
    {
        var id = IdFor(server);
        lock (_gate) return _state.Entries.FirstOrDefault(s => s.Id == id);
    }

    /// <summary>
    /// Server setup's commit: upsert and activate the server. A server that is already saved reopens its most
    /// recently used account that still has a session; otherwise no account is open and sign-in follows.
    /// </summary>
    public void AddAndActivate(Uri server, string? name = null, string? verifiedServerId = null)
    {
        lock (_gate)
        {
            var id = IdFor(server);
            var old = _state.Entries.FirstOrDefault(s => s.Id == id);
            var entry = (old ?? new SavedServer(id, ServerAddress.ToBaseUrl(server))) with
            {
                FetchedName = name ?? old?.FetchedName,
                VerifiedServerId = verifiedServerId ?? old?.VerifiedServerId,
                LastUsedAtEpochMs = Now(),
            };
            var state = _state with { Entries = [.. _state.Entries.Where(s => s.Id != id), entry], ActiveServerId = id };
            Commit(state with { ActiveAccountId = CurrentAccountOf(state, id)?.Id });
        }
    }

    /// <summary>Makes a saved server active with its current account (its most recently used one).</summary>
    public void Activate(string id)
    {
        lock (_gate)
        {
            if (!_state.Entries.Any(s => s.Id == id)) throw new ArgumentException("Saved server was not found.");
            ActivateLocked(id, CurrentAccountOf(_state, id)?.Id);
        }
    }

    /// <summary>
    /// TvChangeAccountViewModel.kt activate: makes <paramref name="accountId"/> on <paramref name="serverId"/> the open
    /// account. Null opens the server with no account, where a new sign-in starts.
    /// </summary>
    public void ActivateAccount(string serverId, string? accountId)
    {
        lock (_gate)
        {
            if (!_state.Entries.Any(s => s.Id == serverId)) throw new ArgumentException("Saved server was not found.");
            if (accountId is not null && !_state.Accounts.Any(a => a.Id == accountId && a.ServerId == serverId))
                throw new ArgumentException("Saved account was not found.");
            ActivateLocked(serverId, accountId);
        }
    }

    private void ActivateLocked(string serverId, string? accountId)
    {
        var now = Now();
        Commit(_state with
        {
            Entries = [.. _state.Entries.Select(s => s.Id == serverId ? s with { LastUsedAtEpochMs = now } : s)],
            Accounts = [.. _state.Accounts.Select(a => a.Id == accountId ? a with { LastUsedAtEpochMs = now } : a)],
            ActiveServerId = serverId,
            ActiveAccountId = accountId,
        });
    }

    public void UpdateActive(Func<SavedServer, SavedServer> update)
    {
        lock (_gate)
        {
            Commit(_state with
            {
                Entries = [.. _state.Entries.Select(s => s.Id == _state.ActiveServerId ? Pin(s, update(s)) : s)],
            });
        }
    }

    /// <summary>Updates the open account; nothing happens while no account is open.</summary>
    public void UpdateActiveAccount(Func<SavedAccount, SavedAccount> update)
    {
        lock (_gate)
        {
            if (_state.ActiveAccountId is not { } id) return;
            Commit(_state with { Accounts = [.. _state.Accounts.Select(a => a.Id == id ? Pin(a, update(a)) : a)] });
        }
    }

    /// <summary>
    /// A sign-in finished on the active server. An account that is already saved there (same server user id, else
    /// the same username) is refreshed instead of duplicated; the open account being signed in again is refreshed
    /// unless the sign-in is provably someone else. Otherwise a new account is saved. Returns the now-open account.
    /// </summary>
    public SavedAccount AdoptSignIn(string? serverUserId, string? username)
    {
        lock (_gate)
        {
            var server = _state.Entries.FirstOrDefault(s => s.Id == _state.ActiveServerId)
                ?? throw new InvalidOperationException("Sign-in needs an active server.");
            var accounts = _state.Accounts.Where(a => a.ServerId == server.Id).ToArray();
            var open = accounts.FirstOrDefault(a => a.Id == _state.ActiveAccountId);
            var match = accounts.FirstOrDefault(a => SameUser(a, serverUserId, username) == true)
                ?? (open is not null && SameUser(open, serverUserId, username) != false ? open : null);
            var now = Now();
            SavedAccount account;
            var entries = _state.Entries;
            if (match is not null)
            {
                account = match with
                {
                    ServerUserId = serverUserId ?? match.ServerUserId,
                    Username = string.IsNullOrWhiteSpace(username) ? match.Username : username,
                    NeedsSignIn = false,
                    LastUsedAtEpochMs = now,
                };
            }
            else
            {
                var id = Guid.NewGuid().ToString("N");
                account = new SavedAccount(id, server.Id, $"{server.Url}#{id}", serverUserId,
                    string.IsNullOrWhiteSpace(username) ? null : username, AddedAtEpochMs: now, LastUsedAtEpochMs: now,
                    AdoptsLegacyData: server.LegacyDataUnclaimed);
                if (server.LegacyDataUnclaimed)
                    entries = [.. entries.Select(s => s.Id == server.Id ? s with { LegacyDataUnclaimed = false } : s)];
            }
            Commit(_state with
            {
                Entries = [.. entries.Select(s => s.Id == server.Id ? s with { LastUsedAtEpochMs = now } : s)],
                Accounts = [.. _state.Accounts.Where(a => a.Id != account.Id), account],
                ActiveAccountId = account.Id,
            });
            return account;
        }
    }

    /// <summary>The server refused this account's credentials. Not a sign-out: the account stays (AccountEntry.kt).</summary>
    public void MarkNeedsSignIn(string accountId)
    {
        lock (_gate)
        {
            Commit(_state with
            {
                Accounts = [.. _state.Accounts.Select(a => a.Id == accountId ? a with { NeedsSignIn = true, AccessTokenExpiresAt = null } : a)],
            });
        }
    }

    /// <summary>
    /// Sign Out of one account (TvSignOutDialog.kt): the account leaves this device and its server stays saved, unless
    /// <paramref name="removeServer"/> also forgets the server (offered only for its last account). The caller forgets
    /// the returned accounts' vault slots after this durable write.
    /// </summary>
    public IReadOnlyList<SavedAccount> RemoveAccount(string accountId, bool removeServer = false)
    {
        lock (_gate)
        {
            var account = _state.Accounts.FirstOrDefault(a => a.Id == accountId) ?? throw new ArgumentException("Saved account was not found.");
            if (removeServer) return RemoveLocked(account.ServerId);
            Commit(_state with
            {
                Accounts = [.. _state.Accounts.Where(a => a.Id != accountId)],
                ActiveAccountId = _state.ActiveAccountId == accountId ? null : _state.ActiveAccountId,
            });
            return [account];
        }
    }

    /// <summary>
    /// Remove Server forgets the server and every account on it. Removing the active server promotes the most recently
    /// used remaining server and its current account (AndroidServerRegistry.kt). Returns the removed accounts.
    /// </summary>
    public IReadOnlyList<SavedAccount> Remove(string id)
    {
        lock (_gate) return RemoveLocked(id);
    }

    private SavedAccount[] RemoveLocked(string id)
    {
        var removed = _state.Accounts.Where(a => a.ServerId == id).ToArray();
        var state = _state with
        {
            Entries = [.. _state.Entries.Where(s => s.Id != id)],
            Accounts = [.. _state.Accounts.Where(a => a.ServerId != id)],
        };
        if (_state.ActiveServerId == id)
        {
            var next = state.Entries.OrderByDescending(s => s.LastUsedAtEpochMs).FirstOrDefault()?.Id;
            state = state with { ActiveServerId = next, ActiveAccountId = next is null ? null : CurrentAccountOf(state, next)?.Id };
        }
        Commit(state);
        return removed;
    }

    /// <summary>
    /// Spec §4.4: servers with no account are removed at launch (servers saved before accounts whose session had been
    /// signed out), except the active one, which the app is signing in to.
    /// </summary>
    public int RemoveAccountlessServers()
    {
        lock (_gate)
        {
            var keep = _state.Entries.Where(s => s.Id == _state.ActiveServerId || _state.Accounts.Any(a => a.ServerId == s.Id)).ToArray();
            var removed = _state.Entries.Length - keep.Length;
            if (removed > 0) Commit(_state with { Entries = keep });
            return removed;
        }
    }

    /// <summary>
    /// TvAppNavigation.kt start destination with spec §4.2: Automatic restores the last account and profile; when
    /// selection is required and two or more accounts are saved, Change Account shows instead of the picker. With no
    /// account open, Change Account is the root while another account still has a session; otherwise sign-in.
    /// </summary>
    public AccountRoute Route(bool selectionRequired, Func<SavedAccount, bool> hasSession)
    {
        var state = Snapshot;
        if (state.Entries.Length == 0 || state.ActiveServerId is null) return AccountRoute.ServerEntry;
        var open = state.Accounts.FirstOrDefault(a => a.Id == state.ActiveAccountId);
        if (open is not null && !open.NeedsSignIn && hasSession(open))
            return selectionRequired && state.Accounts.Length >= 2 ? AccountRoute.ChangeAccount : AccountRoute.Restore;
        return state.Accounts.Any(a => a.Id != open?.Id && !a.NeedsSignIn && hasSession(a)) ? AccountRoute.ChangeAccount : AccountRoute.SignIn;
    }

    public bool RequiresHttpApproval(Uri server)
    {
        var digest = ServerSetupAddress.OriginDigest(server);
        lock (_gate) return digest is not null && !_state.ApprovedHttpOrigins.Contains(digest, StringComparer.Ordinal);
    }

    public void ApproveHttp(Uri server)
    {
        var digest = ServerSetupAddress.OriginDigest(server) ?? throw new ArgumentException("Cleartext approval requires a valid HTTP origin.");
        lock (_gate) Commit(_state with { ApprovedHttpOrigins = [.. _state.ApprovedHttpOrigins.Append(digest).Distinct()] });
    }

    /// <summary>Identity: true when provably the same user, false when provably different, null when unknown.</summary>
    private static bool? SameUser(SavedAccount account, string? serverUserId, string? username)
    {
        if (!string.IsNullOrEmpty(account.ServerUserId) && !string.IsNullOrEmpty(serverUserId))
            return string.Equals(account.ServerUserId, serverUserId, StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(account.Username) && !string.IsNullOrWhiteSpace(username))
            return string.Equals(account.Username, username, StringComparison.OrdinalIgnoreCase);
        return null;
    }

    /// <summary>ServerEntry.kt accountId: the active account on the active server, otherwise the most recently used.</summary>
    private static SavedAccount? CurrentAccountOf(SavedServerState state, string serverId) =>
        state.Accounts.Where(a => a.ServerId == serverId).OrderBy(a => a.NeedsSignIn).ThenByDescending(a => a.LastUsedAtEpochMs).FirstOrDefault();

    private static SavedServer[] Order(SavedServerState state) =>
        [.. state.Entries.OrderByDescending(s => s.Id == state.ActiveServerId).ThenByDescending(s => s.LastUsedAtEpochMs)];

    /// <summary>Identity fields never change through an update.</summary>
    private static SavedServer Pin(SavedServer old, SavedServer updated) => updated with { Id = old.Id, Url = old.Url };
    private static SavedAccount Pin(SavedAccount old, SavedAccount updated) =>
        updated with { Id = old.Id, ServerId = old.ServerId, CredentialKey = old.CredentialKey, AdoptsLegacyData = old.AdoptsLegacyData };

    /// <summary>
    /// Spec §5.2: each server with a saved session becomes one account that keeps its EXACT vault slot and adopts the
    /// server's existing local data, offline and with no new sign-in. A server without a session keeps its legacy data
    /// for the next account that signs in there.
    /// </summary>
    private SavedServerState Migrate(SavedServerMigration migration)
    {
        V1Server[] entries;
        string? active;
        string[] approved;
        if (migration.V1Json is { } json)
        {
            var v1 = JsonSerializer.Deserialize(json, SavedServerJson.Default.V1State) ?? throw new JsonException("Saved server list is unreadable.");
            if (v1.Entries is null || v1.ApprovedHttpOrigins is null
                || v1.Entries.Any(s => s is null || !IsCanonical(s.Url, s.Id) || string.IsNullOrWhiteSpace(s.CredentialKey))
                || v1.Entries.Select(s => s.Id).Distinct().Count() != v1.Entries.Length
                || (v1.Entries.Length == 0 ? v1.ActiveServerId is not null : !v1.Entries.Any(s => s.Id == v1.ActiveServerId)))
            {
                throw new JsonException("Saved server list is unreadable.");
            }
            (entries, active, approved) = (v1.Entries, v1.ActiveServerId, v1.ApprovedHttpOrigins);
        }
        else if (migration.Legacy is { ServerUrl: { } url } legacy && ServerAddress.TryParse(url, out var address))
        {
            // Keep the EXACT old vault username even if normalization changes the spelling.
            var entry = new V1Server(IdFor(address), ServerAddress.ToBaseUrl(address), url, legacy.FetchedName, legacy.ProfileId,
                legacy.ProfileName, legacy.ProfileAvatar, legacy.AccountName, legacy.AccessTokenExpiresAt, Now());
            (entries, active, approved) = ([entry], entry.Id, []);
        }
        else
        {
            return Empty;
        }

        var servers = new List<SavedServer>();
        var accounts = new List<SavedAccount>();
        foreach (var entry in entries)
        {
            var signedIn = migration.HasCredentials(entry.CredentialKey);
            servers.Add(new SavedServer(entry.Id, entry.Url, entry.FetchedName, entry.LastUsedAtEpochMs, entry.VerifiedServerId,
                LegacyDataUnclaimed: !signedIn));
            if (!signedIn) continue;
            accounts.Add(new SavedAccount(Guid.NewGuid().ToString("N"), entry.Id, entry.CredentialKey, null, entry.AccountName,
                entry.ProfileId, entry.ProfileName, entry.ProfileAvatar, entry.AccessTokenExpiresAt, null,
                entry.LastUsedAtEpochMs, entry.LastUsedAtEpochMs, AdoptsLegacyData: true));
        }
        return new SavedServerState([.. servers], [.. accounts], active, accounts.FirstOrDefault(a => a.ServerId == active)?.Id, approved);
    }

    private static bool IsCanonical(string? url, string? id) =>
        url is not null && ServerAddress.TryParse(url, out var server) && url == ServerAddress.ToBaseUrl(server) && id == IdFor(server);

    private static SavedServerState Validate(SavedServerState? state)
    {
        if (state is null || state.Entries is null || state.Accounts is null || state.ApprovedHttpOrigins is null
            || state.Entries.Any(s => s is null || !IsCanonical(s.Url, s.Id))
            || state.Entries.Select(s => s.Id).Distinct().Count() != state.Entries.Length
            || (state.Entries.Length == 0 ? state.ActiveServerId is not null : !state.Entries.Any(s => s.Id == state.ActiveServerId))
            || state.Accounts.Any(a => a is null || string.IsNullOrWhiteSpace(a.Id) || string.IsNullOrWhiteSpace(a.CredentialKey)
                || !state.Entries.Any(s => s.Id == a.ServerId))
            || state.Accounts.Select(a => a.Id).Distinct().Count() != state.Accounts.Length
            || state.Accounts.Select(a => a.CredentialKey).Distinct(StringComparer.Ordinal).Count() != state.Accounts.Length
            || (state.ActiveAccountId is { } open && !state.Accounts.Any(a => a.Id == open && a.ServerId == state.ActiveServerId)))
        {
            throw new JsonException("Saved server list is unreadable.");
        }
        return state;
    }

    private long Now() => _time.GetUtcNow().ToUnixTimeMilliseconds();
    private static string Serialize(SavedServerState state) => JsonSerializer.Serialize(state, SavedServerJson.Default.SavedServerState);
    private void Commit(SavedServerState state)
    {
        _storage.Write(Serialize(state));
        _state = state;
    }

    /// <summary>The one-account-per-server layout (servers.v1.json), read only by migration.</summary>
    internal sealed record V1Server(
        string Id, string Url, string CredentialKey, string? FetchedName = null, string? ProfileId = null,
        string? ProfileName = null, string? ProfileAvatar = null, string? AccountName = null,
        DateTimeOffset? AccessTokenExpiresAt = null, long LastUsedAtEpochMs = 0, string? VerifiedServerId = null);

    internal sealed record V1State(V1Server[] Entries, string? ActiveServerId, string[] ApprovedHttpOrigins);
}

/// <summary>A same-directory replace keeps a failed write from destroying the previous list.</summary>
public sealed class FileSavedServerStorage(string path) : ISavedServerStorage
{
    public string? Read() => File.Exists(path) ? File.ReadAllText(path) : null;
    public void Write(string json)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, path, overwrite: true);
    }
}

[JsonSerializable(typeof(SavedServerState))]
[JsonSerializable(typeof(SavedServerRegistry.V1State))]
internal partial class SavedServerJson : JsonSerializerContext;
