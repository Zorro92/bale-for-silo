using Bale.Core.Connection;

namespace Bale.Xbox.Services;

/// <summary>Each saved account's tokens in its own PasswordVault slot (its CredentialKey), for background reads.</summary>
internal sealed class VaultAccountCredentials : IAccountCredentials
{
    public static VaultAccountCredentials Instance { get; } = new();

    private VaultAccountCredentials()
    {
    }

    public string? LoadRefreshToken(SavedAccount account) => AppSettings.LoadRefreshToken(account);

    public string? LoadAccessToken(SavedAccount account) => AppSettings.LoadAccessToken(account);

    public void SaveTokens(SavedAccount account, string refreshToken, string? accessToken)
    {
        AppSettings.SaveRefreshToken(account, refreshToken);
        AppSettings.SaveAccessToken(account, accessToken);
    }
}
