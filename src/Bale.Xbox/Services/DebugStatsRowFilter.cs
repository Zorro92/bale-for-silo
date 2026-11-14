#if DEBUG_SERVER
using System.Text;

namespace Bale.Xbox.Services;

/// <summary>Memory-only overlay selection; the debug server and player access it on the UI thread.</summary>
internal sealed class DebugStatsRowFilter
{
    internal const string Usage = "expected list|hide <labels>|show <labels>|only <labels>|reset; labels are one non-empty comma-separated argument";
    // Every row label the player can show, so commands sent before a player opens are not reported unknown.
    internal static readonly string[] KnownLabels =
    [
        "Backend", "Route", "Subtitles", "Hard containers", "Video codec", "Resolution", "Frame rate", "HDR mode",
        "Video decoder", "Audio codec", "Audio decoder", "Estimated bandwidth", "Dropped frames", "Audio underruns",
        "Delivery", "Source", "Media bitrate", "Buffering", "HDR output", "Display mode", "Late frames",
        "Stream waits", "Read-ahead", "Stream reads", "FFmpeg HDR", "Video media type (track)", "Video media type (mss)",
        "Sample memory", "Delivered ahead", "CPU",
    ];

    // No route exposes render drops, so that row always reads "unavailable"; hide it until asked for.
    private static readonly string[] DefaultHidden = ["Dropped frames"];

    private readonly HashSet<string> _labels = new(DefaultHidden, StringComparer.OrdinalIgnoreCase);
    private bool _only;

    internal bool IsHidden(string label) => _only ? !_labels.Contains(label) : _labels.Contains(label);

    internal bool TryApply(string? action, string? labels, IEnumerable<string> knownLabels, out IReadOnlyList<string> unknownLabels)
    {
        unknownLabels = [];
        if (action is "list" or "reset")
        {
            if (labels is not null) return false;
            if (action == "reset")
            {
                _labels.Clear();
                _labels.UnionWith(DefaultHidden);
                _only = false;
            }
            return true;
        }
        if (action is not ("hide" or "show" or "only") || string.IsNullOrWhiteSpace(labels) || labels.Any(char.IsControl))
            return false;
        var requested = labels.Split(',').Select(label => label.Trim()).ToArray();
        if (requested.Any(label => label.Length == 0)) return false;
        unknownLabels = requested.Except(knownLabels.Concat(KnownLabels), StringComparer.OrdinalIgnoreCase).ToArray();
        switch (action)
        {
            case "hide":
                if (_only) _labels.ExceptWith(requested);
                else _labels.UnionWith(requested);
                break;
            case "show":
                if (_only) _labels.UnionWith(requested);
                else _labels.ExceptWith(requested);
                break;
            case "only":
                // Retain an allowlist, not just today's hidden rows: later rows must stay hidden too.
                _labels.Clear();
                _labels.UnionWith(requested);
                _only = true;
                break;
        }
        return true;
    }

    internal string DescribeLabels(IReadOnlyList<(string Label, string Value)> rows)
    {
        if (rows.Count == 0) return "no current stat rows\n";
        var builder = new StringBuilder();
        foreach (var (label, _) in rows)
            builder.Append(label).Append(": ").Append(IsHidden(label) ? "off" : "on").Append('\n');
        return builder.ToString();
    }

    internal string DescribeStats(IReadOnlyList<(string Label, string Value)> rows)
    {
        var builder = new StringBuilder();
        // Consume only the player's sanitized snapshot, never plans, stream URLs or exception messages.
        foreach (var (label, value) in rows)
            builder.Append(label).Append(": ").Append(value.Replace("\r", "\\r").Replace("\n", "\\n"))
                .Append(IsHidden(label) ? " [hidden]" : string.Empty).Append('\n');
        return builder.ToString();
    }
}
#endif
