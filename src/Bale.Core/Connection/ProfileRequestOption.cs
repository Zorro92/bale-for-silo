using Microsoft.Kiota.Abstractions;

namespace Bale.Core.Connection;

/// <summary>ProfileApi.kt actingAs / withoutProfile: override profile headers for this request, including after bearer refresh.</summary>
internal sealed class ProfileRequestOption(string? profileId = null, string? profileToken = null, Func<bool>? isCurrent = null) : IRequestOption
{
    public string? ProfileId { get; } = profileId;
    public string? ProfileToken { get; } = profileToken;
    public void CheckCurrent()
    {
        if (isCurrent?.Invoke() == false) throw new OperationCanceledException("The profile management account changed.");
    }
}
