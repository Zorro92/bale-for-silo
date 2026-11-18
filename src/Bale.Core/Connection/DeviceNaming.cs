using System.Text;

namespace Bale.Core.Connection;

/// <summary>
/// The name this device gives itself to a server: the name its owner gave it, as Android TV's
/// <c>TvDeviceName</c> (silo-android android-shared/.../pairing/TvDeviceName.kt) and Apple TV's
/// <c>UIDevice.current.name</c> do, so the sign-in approval page, the account's device list and the
/// TV's own screen agree. The hardware model is only the fallback.
/// </summary>
public static class DeviceNaming
{
    /// <summary>TvDeviceName.MAX_UTF8_BYTES: one cut keeps every place showing the same name and stays well
    /// inside the server's device-name limits.</summary>
    public const int MaxUtf8Bytes = 63;

    /// <summary>The trimmed <paramref name="ownName"/> cut to <see cref="MaxUtf8Bytes"/>, else the fitted
    /// <paramref name="fallback"/> (the hardware model), else "Xbox".</summary>
    public static string Choose(string? ownName, string? fallback)
    {
        var fitted = Fitted(ownName);
        if (fitted.Length == 0) fitted = Fitted(fallback);
        return fitted.Length > 0 ? fitted : "Xbox";
    }

    /// <summary><paramref name="name"/> trimmed and cut to <see cref="MaxUtf8Bytes"/> without splitting a character.</summary>
    public static string Fitted(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        var bytes = 0;
        var end = 0;
        while (end < trimmed.Length)
        {
            var size = char.IsSurrogatePair(trimmed, end) ? 2 : 1;
            var utf8 = Encoding.UTF8.GetByteCount(trimmed.AsSpan(end, size));
            if (bytes + utf8 > MaxUtf8Bytes) break;
            bytes += utf8;
            end += size;
        }
        return trimmed[..end].TrimEnd();
    }
}
