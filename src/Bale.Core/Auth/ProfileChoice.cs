using Bale.Core.Api.Models;

namespace Bale.Core.Auth;

/// <summary>Whether the TV can go straight to Home or has to ask "Who's watching?".</summary>
public static class ProfileChoice
{
    /// <summary>
    /// The profile to act as without asking, or null to show the picker. A remembered profile is
    /// reused unless it has a PIN; a lone unlocked profile is used outright.
    /// </summary>
    public static Profile? Automatic(IReadOnlyList<Profile> profiles, string? rememberedId)
    {
        var remembered = profiles.FirstOrDefault(profile => profile.Id == rememberedId);
        if (remembered is not null)
        {
            return remembered.HasPin == true ? null : remembered;
        }

        return profiles is [{ HasPin: not true } only] ? only : null;
    }
}
