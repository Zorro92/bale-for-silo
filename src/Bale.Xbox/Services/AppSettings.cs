using Windows.Security.Credentials;
using Windows.Storage;

namespace Bale.Xbox.Services;

/// <summary>Per-install state. The refresh token lives in the credential vault, not plain settings.</summary>
internal static class AppSettings
{
    private const string VaultResource = "Bale.Silo";

    private static ApplicationDataContainer Local => ApplicationData.Current.LocalSettings;

    /// <summary>Generated on first launch and kept for the life of the install.</summary>
    public static string DeviceId
    {
        get
        {
            if (Local.Values[nameof(DeviceId)] is string id)
            {
                return id;
            }

            id = Guid.NewGuid().ToString("N");
            Local.Values[nameof(DeviceId)] = id;
            return id;
        }
    }

    public static string? ServerUrl
    {
        get => Local.Values[nameof(ServerUrl)] as string;
        set => Local.Values[nameof(ServerUrl)] = value;
    }

    public static string? LoadRefreshToken(string server)
    {
        try
        {
            var credential = new PasswordVault().Retrieve(VaultResource, server);
            credential.RetrievePassword();
            return credential.Password;
        }
        catch (Exception)
        {
            // Retrieve throws when nothing is stored.
            return null;
        }
    }

    public static void SaveRefreshToken(string server, string refreshToken)
    {
        ForgetRefreshToken(server);
        new PasswordVault().Add(new PasswordCredential(VaultResource, server, refreshToken));
    }

    public static void ForgetRefreshToken(string server)
    {
        var vault = new PasswordVault();
        try
        {
            vault.Remove(vault.Retrieve(VaultResource, server));
        }
        catch (Exception)
        {
            // Nothing stored.
        }
    }
}
