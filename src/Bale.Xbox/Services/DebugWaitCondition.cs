#if DEBUG_SERVER
using System.Globalization;

namespace Bale.Xbox.Services;

internal sealed record DebugWaitCondition(string Kind, string Value, int TimeoutMs)
{
    internal const int DefaultTimeoutMs = 8000;
    internal const int MaximumTimeoutMs = 15000;

    internal static bool TryParse(string? condition, string? timeout, out DebugWaitCondition result, out string error)
    {
        result = new(string.Empty, string.Empty, DefaultTimeoutMs);
        error = "for must be loaded, page <substring>, text <substring>, or focus <substring>";
        var parts = (condition ?? string.Empty).Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        var kind = parts[0].ToLowerInvariant();
        var value = parts.Length == 2 ? parts[1].Trim() : string.Empty;
        if (kind == "loaded" ? value.Length != 0 : kind is not ("page" or "text" or "focus") || value.Length == 0) return false;
        var timeoutMs = DefaultTimeoutMs;
        if (timeout is not null && (!int.TryParse(timeout, NumberStyles.None, CultureInfo.InvariantCulture, out timeoutMs)
            || timeoutMs > MaximumTimeoutMs))
        {
            error = $"timeout must be an integer from 0 to {MaximumTimeoutMs} ms";
            return false;
        }
        result = new(kind, value, timeoutMs);
        error = string.Empty;
        return true;
    }
}
#endif
