#if DEBUG_SERVER
using System.Text;

namespace Bale.Xbox.Services;

internal sealed record DebugVisibleControl(string Group, string Label, bool Focused = false,
    bool Checked = false, bool Selected = false, bool Disabled = false);

/// <summary>Bounded, single-line debug output; kept free of UWP so Linux tests exercise the caps.</summary>
internal static class DebugScreenText
{
    internal static string Compact(string text, int maxLength = 160)
    {
        var line = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= maxLength ? line : line[..(maxLength - 3)] + "...";
    }

    internal static string Visible(IEnumerable<DebugVisibleControl> controls, int perGroup = 12, int maxGroups = 12)
    {
        var groups = controls.GroupBy(control => control.Group).ToList();
        if (groups.Count == 0) return "Visible: none\n";
        var builder = new StringBuilder("Visible:\n");
        foreach (var group in groups.Take(maxGroups))
        {
            builder.Append("  ").Append(Compact(group.Key, 100)).Append(":\n");
            foreach (var control in group.Take(perGroup))
            {
                builder.Append("    ").Append(Compact(control.Label, 180));
                if (control.Focused) builder.Append(" [focused]");
                if (control.Checked) builder.Append(" [checked]");
                if (control.Selected) builder.Append(" [selected]");
                if (control.Disabled) builder.Append(" [disabled]");
                builder.Append('\n');
            }
            if (group.Count() > perGroup) builder.Append($"    +{group.Count() - perGroup} more\n");
        }
        if (groups.Count > maxGroups) builder.Append($"  +{groups.Count - maxGroups} more groups\n");
        return builder.ToString();
    }
}
#endif
