namespace Bale.Core.Connection;

/// <summary>Where each saved account's tokens live (the platform vault on Xbox, under its CredentialKey).</summary>
public interface IAccountCredentials
{
    string? LoadRefreshToken(SavedAccount account);
    string? LoadAccessToken(SavedAccount account);
    void SaveTokens(SavedAccount account, string refreshToken, string? accessToken);
}

/// <summary>Reads how many profiles a saved account has; null when that could not be read.</summary>
public delegate Task<int?> AccountProfileCountSource(SavedServer server, SavedAccount account, CancellationToken cancellationToken);

/// <summary>
/// AccountProfilesRefresher.kt: Change Account renders each account's last known profile count straight away and
/// refreshes every saved account's list in the background once, when it opens (TvChangeAccountViewModel.kt init). An
/// account that must sign in again is skipped; a server that can't be reached, or a session it refuses, keeps the last
/// count. Refreshing never signs anyone out, marks an account "Sign in again" or asks for anything.
/// </summary>
public sealed class AccountProfilesRefresher(SavedServerRegistry registry, AccountProfileCountSource source)
{
    /// <summary>Refreshes every saved account at once. Returns the accounts whose count changed.</summary>
    public async Task<IReadOnlyList<string>> RefreshAllAsync(CancellationToken cancellationToken = default)
    {
        var state = registry.Snapshot;
        var work = state.Accounts
            .Where(account => !account.NeedsSignIn)
            .Select(account => (Server: state.Entries.FirstOrDefault(s => s.Id == account.ServerId), Account: account))
            .Where(pair => pair.Server is not null)
            .Select(pair => RefreshAsync(pair.Server!, pair.Account, cancellationToken))
            .ToArray();
        var changed = await Task.WhenAll(work).ConfigureAwait(false);
        return [.. changed.OfType<string>()];
    }

    private async Task<string?> RefreshAsync(SavedServer server, SavedAccount account, CancellationToken cancellationToken)
    {
        int? count;
        try
        {
            count = await source(server, account, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // AccountProfilesRefresher.kt: anything but a success keeps the account's last list.
            return null;
        }
        if (count is not int profiles || profiles < 0 || cancellationToken.IsCancellationRequested) return null;
        try
        {
            return registry.RecordProfileCount(account.Id, profiles) ? account.Id : null;
        }
        catch (Exception)
        {
            // A cached count is cosmetic; a failed write must not fail Change Account.
            return null;
        }
    }

    /// <summary>
    /// The real source: one short-lived client per account with that account's own saved tokens, never the open
    /// session (AuthScopeSnapshot.profileless for the saved account). A refresh the server refuses only fails this
    /// read. A refresh it grants rotates the tokens, which go back to that account's own slot, unless the account is
    /// open now (the live session owns its slot), was removed, or its slot changed since the read began.
    /// </summary>
    public static AccountProfileCountSource FromSavedSessions(
        SavedServerRegistry registry, IAccountCredentials credentials, Func<Uri, SiloClient> newClient)
    {
        var saveGate = new object();
        return async (server, account, cancellationToken) =>
        {
            var url = new Uri(server.Url);
            // TvServerResolver.kt never lets stored credentials reach an unapproved HTTP origin.
            if (registry.RequiresHttpApproval(url)) return null;
            if (credentials.LoadRefreshToken(account) is not { Length: > 0 } saved) return null;
            using var client = newClient(url);
            client.RestoreSession(saved, credentials.LoadAccessToken(account), account.AccessTokenExpiresAt);
            client.TokensRefreshed += (_, _) =>
            {
                lock (saveGate)
                {
                    if (client.RefreshToken is not { Length: > 0 } rotated || rotated == saved) return;
                    if (registry.ActiveAccount?.Id == account.Id) return;
                    if (!registry.Snapshot.Accounts.Any(a => a.Id == account.Id && !a.NeedsSignIn)) return;
                    try
                    {
                        if (credentials.LoadRefreshToken(account) != saved) return;
                        credentials.SaveTokens(account, rotated, client.AccessToken);
                        registry.UpdateAccount(account.Id, a => a with { AccessTokenExpiresAt = client.AccessTokenExpiresAt });
                        saved = rotated;
                    }
                    catch (Exception)
                    {
                        // The saved pair still signs in (the server checks the session, not the token's age).
                    }
                }
            };
            var profiles = await client.GetProfilesAsync(cancellationToken).ConfigureAwait(false);
            return profiles.Count;
        };
    }
}
