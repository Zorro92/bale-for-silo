using Bale.Core.Api.Models;

namespace Bale.Core.Auth;

/// <summary>Whether the TV can go straight to Home or has to ask "Who's watching?".</summary>
public static class ProfileChoice
{
    /// <summary>
    /// A new TV asks every time, even with one profile. Only an explicit answer to the remember
    /// offer enables automatic opening (ProfileLaunchPreferences.kt firstLaunch/answeringRememberOffer).
    /// This local decision must not wait for the network at startup (MainTvActivity.kt).
    /// </summary>
    public static string? AutomaticId(string? rememberedId, bool openAutomatically = false) =>
        openAutomatically && !string.IsNullOrWhiteSpace(rememberedId) ? rememberedId : null;

    /// <summary>
    /// Resolves the saved choice against a loaded list. Automatic also reopens protected profiles;
    /// the app restores their secure PIN proof, never a saved PIN (ProfileLaunchPreferences.kt).
    /// A missing/deleted choice returns to the picker rather than choosing a different person.
    /// </summary>
    public static Profile? Automatic(IReadOnlyList<Profile> profiles, string? rememberedId, bool openAutomatically = false) =>
        AutomaticId(rememberedId, openAutomatically) is { } id
            ? profiles.FirstOrDefault(profile => profile.Id == id)
            : null;
}
