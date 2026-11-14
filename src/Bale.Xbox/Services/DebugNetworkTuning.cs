#if DEBUG_SERVER
using System.Globalization;
using Bale.Core.Connection;
using Windows.Storage;

namespace Bale.Xbox.Services;

/// <summary>
/// `bale-debug.sh net [rcvbuf=<bytes|Nk|Nm|default>]`: the socket receive buffer for Bale's own HTTP
/// connections. Saved locally and applied at the next launch (pooled connections keep their buffer).
/// </summary>
internal static class DebugNetworkTuning
{
    private const string Key = "debug.socketReceiveBuffer";

    internal static void ApplySaved()
    {
        try
        {
            if (ApplicationData.Current.LocalSettings.Values[Key] is int bytes && bytes > 0)
            {
                NetworkTuning.SocketReceiveBufferBytes = bytes;
                Log.Info($"network: socket receive buffer {bytes} bytes (debug)");
            }
        }
        catch { }
    }

    internal static string Status() => "Socket receive buffer: active="
        + (NetworkTuning.SocketReceiveBufferBytes?.ToString(CultureInfo.InvariantCulture) ?? "OS default")
        + " saved=" + (ApplicationData.Current.LocalSettings.Values[Key] is int saved ? saved.ToString(CultureInfo.InvariantCulture) : "default") + "\n";

    internal static (string Status, string Body) Set(string? value)
    {
        if (value is null) return ("200 OK", Status());
        if (value == "default")
        {
            ApplicationData.Current.LocalSettings.Values.Remove(Key);
            return ("200 OK", Status() + "applies at the next launch\n");
        }
        var text = value.Trim().ToLowerInvariant();
        var scale = text.EndsWith('m') ? 1024 * 1024 : text.EndsWith('k') ? 1024 : 1;
        if (scale > 1) text = text[..^1];
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0
            || (long)number * scale is < 4096 or > 64L * 1024 * 1024)
            return ("400 Bad Request", "rcvbuf must be 4k–64m (bytes, Nk or Nm) or default\n");
        ApplicationData.Current.LocalSettings.Values[Key] = number * scale;
        return ("200 OK", Status() + "applies at the next launch\n");
    }
}
#endif
