using Bale.Core.Api.Models;

namespace Bale.Core.Settings;

/// <summary>TvSettingsScreen.kt SettingsAccountRow/accountSubtitle: profile identity over account role.</summary>
public sealed record SettingsAccount(string Name, string Subtitle)
{
    public static SettingsAccount From(Account? account, string? profileName, string? cachedUsername = null)
    {
        var username = NonBlank(account?.Username) ?? NonBlank(cachedUsername);
        return new SettingsAccount(NonBlank(profileName) ?? username ?? "Silo",
            account?.Role == Account_role.Admin ? "Administrator" : username ?? "Signed in");
    }

    private static string? NonBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
}
