namespace Bale.Core.Profiles;

/// <summary>The profile menu's profile row (ProfileMenuRow.kt).</summary>
public enum ProfileMenuRow { SwitchProfile, AddProfile, None }

public static class ProfileMenu
{
    /// <summary>
    /// ProfileMenuRow.kt: Switch Profile while the account has several profiles (or none known),
    /// Add Profile while it has one and may have more, no row while it has one and its limit is one.
    /// A server that sends no <c>max_profiles</c> keeps offering Add Profile.
    /// </summary>
    /// <summary>
    /// canAddProfile (server 451cba6c): Add stays until the household reaches <c>max_profiles</c>; a
    /// server that sends no limit keeps it.
    /// </summary>
    public static bool CanAdd(int profileCount, long? maxProfiles) => maxProfiles is null || profileCount < maxProfiles;

    public static ProfileMenuRow Row(int profileCount, long? maxProfiles) => profileCount switch
    {
        not 1 => ProfileMenuRow.SwitchProfile,
        _ when maxProfiles is null or > 1 => ProfileMenuRow.AddProfile,
        _ => ProfileMenuRow.None,
    };
}
