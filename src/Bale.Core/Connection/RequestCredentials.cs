namespace Bale.Core.Connection;

/// <summary>What a signed-in request carries: the bearer token and, for profile-scoped endpoints, the acting profile.</summary>
/// <param name="ProfileId">The household profile acting for the request; sent as X-Profile-Id.</param>
/// <param name="ProfileToken">Proof of a PIN-locked profile's PIN; sent as X-Profile-Token.</param>
public sealed record RequestCredentials(string AccessToken, string? ProfileId = null, string? ProfileToken = null);
