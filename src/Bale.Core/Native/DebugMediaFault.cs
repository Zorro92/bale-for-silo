#if DEBUG_SERVER
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Bale.Core.Native;

/// <summary>
/// Debug only (<c>bale-debug.sh media-fault</c>): native/bale-core/src/media_fault.rs's memory-only media faults, to
/// watch the playback reliability rules on the console without touching the server, and the Rust streams' recovery
/// counters. Nothing is armed at start, and release builds have no debug server to arm anything.
/// </summary>
public static class DebugMediaFault
{
    public const string Usage = "usage: GET /media-fault or POST /media-fault?outage={0..600}&empty-tail={0..20}\n";
    public const int MaxOutageSeconds = 600;
    public const int MaxEmptyTail = 20;

    /// <summary>
    /// A POST's keys, all validated before anything is armed: <c>outage</c> = seconds from now during which every Rust
    /// media request and open body read fails as a network error (0 ends an armed outage); <c>empty-tail</c> = how many of
    /// a complete playlist's last segments the next HLS mount answers itself with no media, as Silo answers segments past
    /// a finished transcode's real end (0 disarms).
    /// </summary>
    public static bool TryParse(IReadOnlyDictionary<string, string> query, out int? outageSeconds, out int? emptyTail)
    {
        outageSeconds = emptyTail = null;
        if (query.Count == 0 || query.Keys.Any(key => key is not ("outage" or "empty-tail"))) return false;
        if (query.TryGetValue("outage", out var outage))
        {
            if (!int.TryParse(outage, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds > MaxOutageSeconds)
                return false;
            outageSeconds = seconds;
        }
        if (query.TryGetValue("empty-tail", out var tail))
        {
            if (!int.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out var segments) || segments > MaxEmptyTail)
            {
                outageSeconds = null;
                return false;
            }
            emptyTail = segments;
        }
        return true;
    }

    /// <summary>Arms what is given (nothing for null) and describes the armed faults and the counters.</summary>
    public static string Apply(int? outageSeconds, int? emptyTail = null) => Describe(BaleCoreNative.Call("media_fault.set", writer =>
    {
        if (outageSeconds is { } seconds) writer.WriteNumber("outage_seconds", seconds);
        if (emptyTail is { } segments) writer.WriteNumber("empty_tail", segments);
    }, BaleCoreJson.Default.JsonElement));

    /// <summary>One "name: value" line per field Rust reports (numbers only).</summary>
    internal static string Describe(JsonElement state)
    {
        var text = new StringBuilder();
        foreach (var property in state.EnumerateObject())
            text.Append(property.Name.Replace('_', '-')).Append(": ").Append(property.Value.GetRawText()).Append('\n');
        return text.ToString();
    }
}
#endif
