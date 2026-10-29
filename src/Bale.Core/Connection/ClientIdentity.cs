namespace Bale.Core.Connection;

/// <summary>
/// Who this install is, reported to the Silo server on every request so it shows up
/// correctly under the user's devices and admin activity.
/// </summary>
/// <param name="DeviceId">Stable per-install id; generate once and persist it.</param>
/// <param name="DeviceName">Human-readable name shown to the approver, e.g. "Xbox Series X".</param>
/// <param name="Platform">Platform label, e.g. "xbox".</param>
/// <param name="ClientName">App name, e.g. "Bale".</param>
/// <param name="ClientFamily">One of "tv", "mobile", "tablet", "desktop", "web"; the server keys per-client settings on it.</param>
public sealed record ClientIdentity(
    string DeviceId,
    string DeviceName,
    string Platform,
    string ClientName,
    string? ClientVersion = null,
    string? ClientBuild = null,
    string? ClientChannel = null,
    string? ClientFamily = null);
