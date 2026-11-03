#if DEBUG_SERVER
using System.Text;

namespace Bale.Xbox.Services;

/// <summary>Explicit GUID aliases, not reflection or decoder-name guesses; unknown subtypes stay raw.</summary>
internal sealed class DebugCodecNames
{
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);

    internal DebugCodecNames(IEnumerable<(string Subtype, string Name)> names)
    {
        foreach (var (subtype, name) in names) _names[Key(subtype)] = name;
    }

    internal string Describe(string subtype) => _names.TryGetValue(Key(subtype), out var name) ? $"{name} [{subtype}]" : subtype;

    internal string Summary(IEnumerable<string> observedSubtypes, IEnumerable<string> codecNames)
    {
        var supported = observedSubtypes.Select(subtype => _names.GetValueOrDefault(Key(subtype)))
            .OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var builder = new StringBuilder();
        foreach (var name in codecNames.Distinct(StringComparer.OrdinalIgnoreCase))
            builder.Append($"    {name}: supported {(supported.Contains(name) ? "yes" : "no")}\n");
        return builder.ToString();
    }

    private static string Key(string subtype) => Guid.TryParse(subtype, out var guid) ? guid.ToString("D") : subtype;
}
#endif
