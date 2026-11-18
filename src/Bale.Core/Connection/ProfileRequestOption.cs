using Microsoft.Kiota.Abstractions;

namespace Bale.Core.Connection;

/// <summary>ProfileApi.kt actingAs / withoutProfile: override profile headers for this request, including after bearer refresh.</summary>
internal sealed class ProfileRequestOption(string? profileId = null, string? profileToken = null, Func<bool>? isCurrent = null) : IRequestOption
{
    /// <summary>
    /// APIv2Client.swift householdProfileList (Apple b5c73e3a): name no profile, so a deleted active profile cannot
    /// turn the read into a 404, but keep the session's PIN proof on the request as Apple does.
    /// </summary>
    public static ProfileRequestOption AccountKeepingProof() => new() { KeepsSessionToken = true };

    public string? ProfileId { get; } = profileId;
    public string? ProfileToken { get; } = profileToken;
    /// <summary>Send the session's X-Profile-Token instead of <see cref="ProfileToken"/>.</summary>
    public bool KeepsSessionToken { get; private init; }
    public void CheckCurrent()
    {
        if (isCurrent?.Invoke() == false) throw new OperationCanceledException("The profile management account changed.");
    }
}
