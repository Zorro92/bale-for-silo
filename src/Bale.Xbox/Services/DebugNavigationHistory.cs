#if DEBUG_SERVER
using System.Globalization;

namespace Bale.Xbox.Services;

/// <summary>Memory-only, bounded history. Callers supply identities, never screen text or raw requests.</summary>
internal sealed class DebugNavigationHistory
{
    internal const int Capacity = 200;
    internal static DebugNavigationHistory Shared { get; } = new();
    private readonly object _gate = new();
    private readonly Queue<string> _entries = new();

    // A final allowlist protects against URLs/tokens arriving in route parameters or control names.
    internal static string Identity(string? value) => DebugNavigationRequest.IsId(value) ? value! : "redacted";

    internal void Add(string kind, string detail)
    {
        lock (_gate)
        {
            _entries.Enqueue(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " " + kind + " " + detail);
            while (_entries.Count > Capacity) _entries.Dequeue();
        }
    }

    internal string Last(int count)
    {
        lock (_gate)
            return string.Join("\n", _entries.Skip(Math.Max(0, _entries.Count - Math.Clamp(count, 1, Capacity))))
                + (_entries.Count == 0 ? string.Empty : "\n");
    }

    internal static void PlayerEvent(string message)
    {
        // Do not retain arbitrary log messages: native failures can contain stream grants.
        if (message == "player: start") Shared.Add("player", "opening");
        else if (message == "player: opened") Shared.Add("player", "open");
        else if (message == "player: stop") Shared.Add("player", "stop");
    }
}
#endif
