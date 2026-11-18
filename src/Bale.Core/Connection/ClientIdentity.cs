namespace Bale.Core.Connection;

/// <summary>
/// Who this install is, reported to the Silo server on every request so it shows up
/// correctly under the user's devices and admin activity.
/// </summary>
/// <param name="DeviceId">Stable per-install id; generate once and persist it.</param>
/// <param name="DeviceName">The name the owner gave this device (the console's own name), shown to the approver and
/// under the account's devices, as Android TV's TvDeviceName does.</param>
/// <param name="Platform">Platform label, e.g. "xbox".</param>
/// <param name="ClientName">App name, e.g. "Bale".</param>
/// <param name="ClientFamily">One of "tv", "mobile", "tablet", "desktop", "web"; the server keys per-client settings on it.</param>
/// <param name="DeviceModel">Hardware model (e.g. "Xbox Series X"), sent only as the playback device model.</param>
public sealed record ClientIdentity(
    string DeviceId,
    string DeviceName,
    string Platform,
    string ClientName,
    string? ClientVersion = null,
    string? ClientBuild = null,
    string? ClientChannel = null,
    string? ClientFamily = null,
    string? DeviceModel = null);
