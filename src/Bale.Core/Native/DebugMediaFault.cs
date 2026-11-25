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
    public const string Usage = "usage: GET /media-fault or POST /media-fault?outage={0..600}\n";
    public const int MaxOutageSeconds = 600;

    /// <summary>
    /// A POST's keys, all validated before anything is armed: <c>outage</c> = seconds from now during which every Rust
    /// media request and open body read fails as a network error (0 ends an armed outage).
    /// </summary>
    public static bool TryParse(IReadOnlyDictionary<string, string> query, out int? outageSeconds)
    {
        outageSeconds = null;
        if (query.Count == 0 || query.Keys.Any(key => key is not "outage")) return false;
        if (!int.TryParse(query["outage"], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || seconds > MaxOutageSeconds) return false;
        outageSeconds = seconds;
        return true;
    }

    /// <summary>Arms what is given (nothing for null) and describes the armed faults and the counters.</summary>
    public static string Apply(int? outageSeconds) => Describe(BaleCoreNative.Call("media_fault.set", writer =>
    {
        if (outageSeconds is { } seconds) writer.WriteNumber("outage_seconds", seconds);
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
