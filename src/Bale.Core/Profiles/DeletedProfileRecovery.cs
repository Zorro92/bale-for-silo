using Bale.Core.Api.Models;

namespace Bale.Core.Profiles;

/// <summary>
/// Apple b5c73e3a (StartupContentPrefetcher.swift, AuthService.swift reconcileAvailableProfiles): the server answers
/// 404 not_found when a request names a profile that no longer exists. A not_found on Home while a profile is active
/// re-reads the account's profile list, and only a list without the active profile sends the viewer back to Who's
/// watching; a 404 for any other reason leaves a valid profile alone. No Android counterpart (owner, 2026-11-18).
/// </summary>
public static class DeletedProfileRecovery
{
    /// <summary>StartupContentPrefetcher.swift indicatesNotFound: status 404 with the not_found problem type.</summary>
    public static bool IsNotFound(Exception error) => error is Problem problem
        && (problem.ResponseStatusCode == 404 || problem.Status == 404)
        && problem.Type?.Split('/').LastOrDefault() == "not_found";

    /// <summary>AuthService.swift reconcileAvailableProfiles: the active profile is missing from a loaded list.</summary>
    public static bool ActiveProfileMissing(IReadOnlyList<Profile> profiles, string? activeProfileId) =>
        !string.IsNullOrEmpty(activeProfileId) && profiles.All(profile => profile.Id != activeProfileId);
}
