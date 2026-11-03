#if DEBUG_SERVER
using System.Text;

namespace Bale.Xbox.Services;

/// <summary>Explicit GUID aliases; unknown subtypes stay raw, and IEC-61937 converters never imply decoding.</summary>
internal sealed class DebugCodecNames
{
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);

    internal DebugCodecNames(IEnumerable<(string Subtype, string Name)> names)
    {
        foreach (var (subtype, name) in names) _names[Key(subtype)] = name;
    }

    internal string Describe(string subtype) => _names.TryGetValue(Key(subtype), out var name) ? $"{name} [{subtype}]" : subtype;

    internal string Summary(IEnumerable<(string DisplayName, IEnumerable<string> Subtypes)> codecs, IEnumerable<string> codecNames)
    {
        var decoded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var passthrough = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (displayName, subtypes) in codecs)
        {
            // CodecQuery includes bitstream converters in Decoder results; these do not decode the stream.
            var converter = displayName.Contains("IEC-61937", StringComparison.OrdinalIgnoreCase)
                || displayName.Contains("IEC61937", StringComparison.OrdinalIgnoreCase);
            var supported = converter ? passthrough : decoded;
            foreach (var subtype in subtypes)
                if (_names.TryGetValue(Key(subtype), out var name)) supported.Add(name);
        }
        var builder = new StringBuilder();
        foreach (var name in codecNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var description = string.Equals(name, "PCM", StringComparison.OrdinalIgnoreCase) ? "native (no decoder needed)"
                : decoded.Contains(name) ? "decode yes" + (passthrough.Contains(name) ? ", passthrough converter yes" : string.Empty)
                : passthrough.Contains(name) ? "passthrough only (IEC-61937 converter)" : "no";
            builder.Append($"    {name}: {description}\n");
        }
        return builder.ToString();
    }

    private static string Key(string subtype) => Guid.TryParse(subtype, out var guid) ? guid.ToString("D") : subtype;
}
#endif
