using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bale.Core.Connection;

/// <summary>ServerEntry.kt: URL identity, branding, profile and recency; secrets stay in the platform vault.</summary>
public sealed record SavedServer(
    string Id, string Url, string CredentialKey, string? FetchedName = null, string? ProfileId = null,
    string? ProfileName = null, string? ProfileAvatar = null, string? AccountName = null,
    DateTimeOffset? AccessTokenExpiresAt = null, long LastUsedAtEpochMs = 0, string? VerifiedServerId = null)
{
    public string DisplayName => string.IsNullOrWhiteSpace(FetchedName) ? new Uri(Url).Authority : FetchedName;
}

public sealed record SavedServerState(SavedServer[] Entries, string? ActiveServerId, string[] ApprovedHttpOrigins);

/// <summary>The legacy metadata snapshot. Migration never reads, removes or re-keys credential-vault entries.</summary>
public sealed record LegacyServerSession(string? ServerUrl, string? FetchedName = null, string? ProfileId = null,
    string? ProfileName = null, string? ProfileAvatar = null, string? AccountName = null, DateTimeOffset? AccessTokenExpiresAt = null);

public interface ISavedServerStorage
{
    string? Read();
    void Write(string json);
}

/// <summary>One durable blob, as in AndroidServerRegistry.kt. Publish state only after a successful write.</summary>
public sealed class SavedServerRegistry
{
    private readonly ISavedServerStorage _storage;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private SavedServerState _state;

    public SavedServerRegistry(ISavedServerStorage storage, LegacyServerSession? legacy = null, TimeProvider? time = null)
    {
        _storage = storage;
        _time = time ?? TimeProvider.System;
        if (storage.Read() is { } json)
        {
            // An unreadable blob is NOT an empty install: never remigrate or overwrite it.
            _state = JsonSerializer.Deserialize(json, SavedServerJson.Default.SavedServerState)
                ?? throw new JsonException("Saved server list is unreadable.");
            if (_state.Entries is null || _state.ApprovedHttpOrigins is null
                || _state.Entries.Any(s => s is null || !ServerAddress.TryParse(s.Url, out var server)
                    || s.Url != ServerAddress.ToBaseUrl(server) || s.Id != IdFor(server) || string.IsNullOrWhiteSpace(s.CredentialKey))
                || _state.Entries.Select(s => s.Id).Distinct().Count() != _state.Entries.Length
                || (_state.Entries.Length == 0 ? _state.ActiveServerId is not null : !_state.Entries.Any(s => s.Id == _state.ActiveServerId)))
            {
                throw new JsonException("Saved server list is unreadable.");
            }
            return;
        }

        _state = new SavedServerState([], null, []);
        if (legacy?.ServerUrl is { } url && ServerAddress.TryParse(url, out var server))
        {
            var normalized = ServerAddress.ToBaseUrl(server);
            // Keep the EXACT old vault username even if normalization changes the spelling.
            var entry = new SavedServer(IdFor(server), normalized, url, legacy.FetchedName, legacy.ProfileId,
                legacy.ProfileName, legacy.ProfileAvatar, legacy.AccountName, legacy.AccessTokenExpiresAt, Now());
            _state = new SavedServerState([entry], entry.Id, []);
        }
        // The empty blob is also the migration marker: removing the last server must not resurrect legacy state.
        storage.Write(Serialize(_state));
    }

    public SavedServerState Snapshot { get { lock (_gate) return _state with { Entries = [.. _state.Entries], ApprovedHttpOrigins = [.. _state.ApprovedHttpOrigins] }; } }
    public SavedServer? Active { get { lock (_gate) return _state.Entries.FirstOrDefault(s => s.Id == _state.ActiveServerId); } }

    public IReadOnlyList<SavedServer> Sorted
    {
        get
        {
            var state = Snapshot;
            return state.Entries.OrderByDescending(s => s.Id == state.ActiveServerId).ThenByDescending(s => s.LastUsedAtEpochMs).ToArray();
        }
    }

    public static string IdFor(Uri server) => Convert.ToBase64String(Encoding.UTF8.GetBytes(ServerAddress.ToBaseUrl(server)))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public SavedServer? Find(Uri server)
    {
        var id = IdFor(server);
        lock (_gate) return _state.Entries.FirstOrDefault(s => s.Id == id);
    }

    public void AddAndActivate(Uri server, string? name = null, string? verifiedServerId = null)
    {
        lock (_gate)
        {
            var id = IdFor(server);
            var old = _state.Entries.FirstOrDefault(s => s.Id == id);
            var entry = (old ?? new SavedServer(id, ServerAddress.ToBaseUrl(server), ServerAddress.ToBaseUrl(server))) with
            {
                FetchedName = name ?? old?.FetchedName,
                VerifiedServerId = verifiedServerId ?? old?.VerifiedServerId,
                LastUsedAtEpochMs = Now(),
            };
            Commit(_state with { Entries = [.. _state.Entries.Where(s => s.Id != id), entry], ActiveServerId = id });
        }
    }

    public void Activate(string id)
    {
        lock (_gate)
        {
            if (!_state.Entries.Any(s => s.Id == id)) throw new ArgumentException("Saved server was not found.");
            Commit(_state with
            {
                Entries = _state.Entries.Select(s => s.Id == id ? s with { LastUsedAtEpochMs = Now() } : s).ToArray(),
                ActiveServerId = id,
            });
        }
    }

    public void UpdateActive(Func<SavedServer, SavedServer> update)
    {
        lock (_gate)
        {
            Commit(_state with { Entries = _state.Entries.Select(s => s.Id == _state.ActiveServerId ? update(s) : s).ToArray() });
        }
    }

    /// <summary>AndroidServerRegistry.kt promotes the most recently used remaining server, not an arbitrary row.</summary>
    public void Remove(string id)
    {
        lock (_gate)
        {
            var entries = _state.Entries.Where(s => s.Id != id).ToArray();
            var active = _state.ActiveServerId == id ? entries.OrderByDescending(s => s.LastUsedAtEpochMs).FirstOrDefault()?.Id : _state.ActiveServerId;
            Commit(_state with { Entries = entries, ActiveServerId = active });
        }
    }

    public bool RequiresHttpApproval(Uri server)
    {
        var digest = ServerSetupAddress.OriginDigest(server);
        lock (_gate) return digest is not null && !_state.ApprovedHttpOrigins.Contains(digest, StringComparer.Ordinal);
    }

    public void ApproveHttp(Uri server)
    {
        var digest = ServerSetupAddress.OriginDigest(server) ?? throw new ArgumentException("Cleartext approval requires a valid HTTP origin.");
        lock (_gate) Commit(_state with { ApprovedHttpOrigins = _state.ApprovedHttpOrigins.Append(digest).Distinct().ToArray() });
    }

    private long Now() => _time.GetUtcNow().ToUnixTimeMilliseconds();
    private static string Serialize(SavedServerState state) => JsonSerializer.Serialize(state, SavedServerJson.Default.SavedServerState);
    private void Commit(SavedServerState state)
    {
        _storage.Write(Serialize(state));
        _state = state;
    }
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
internal partial class SavedServerJson : JsonSerializerContext;
