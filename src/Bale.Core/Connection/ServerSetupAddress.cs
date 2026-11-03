using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Bale.Core.Connection;

public enum ServerSetupScheme { Auto, Https, Http }

/// <summary>ServerSetupAddress.kt: Advanced options and the ordered HTTPS/HTTP/8090 probe candidates.</summary>
public static class ServerSetupAddress
{
    public static IReadOnlyList<Uri> Candidates(string? input, ServerSetupScheme scheme = ServerSetupScheme.Auto, string? port = null)
    {
        var text = input?.Trim() ?? string.Empty;
        if (text.Length == 0) throw new ArgumentException("Please enter a server host.");
        var hasScheme = text.Contains("://", StringComparison.Ordinal);
        if (text.Any(char.IsWhiteSpace) || !Uri.TryCreate(hasScheme ? text : "https://" + text, UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https") || string.IsNullOrEmpty(parsed.Host) || parsed.UserInfo.Length > 0)
        {
            throw new ArgumentException("Enter a valid server address.");
        }

        // A port in the address wins over the separate field, including an explicit default port.
        var authority = (hasScheme ? text[(text.IndexOf("://", StringComparison.Ordinal) + 3)..] : text).Split('/', '?', '#')[0];
        var portStart = authority.StartsWith('[') ? authority.IndexOf(']') + 1 : authority.IndexOf(':');
        var typedPort = portStart >= 0 && portStart < authority.Length && authority[portStart] == ':'
            ? authority[(portStart + 1)..] : null;
        var explicitPort = ParsePort(typedPort ?? port);
        var schemes = scheme switch
        {
            ServerSetupScheme.Https => new[] { "https" },
            ServerSetupScheme.Http => new[] { "http" },
            _ => hasScheme ? new[] { parsed.Scheme } : ["https", "http"],
        };
        var result = schemes.Select(s => Build(s, parsed, explicitPort)).ToList();
        if (scheme == ServerSetupScheme.Auto && !hasScheme && explicitPort is null)
        {
            result.Add(Build("http", parsed, 8090));
        }
        return result.Distinct().ToArray();
    }

    public static bool UsesCleartext(string? input, ServerSetupScheme scheme = ServerSetupScheme.Auto, string? port = null)
    {
        if (string.IsNullOrWhiteSpace(input)) return false;
        if (scheme == ServerSetupScheme.Http) return true;
        if (scheme == ServerSetupScheme.Https) return false;
        try
        {
            var candidates = Candidates(input, scheme, port);
            return candidates.Count == 1 && candidates[0].Scheme == "http";
        }
        catch (ArgumentException) { return false; }
    }

    /// <summary>CleartextConsentStore.kt: exact canonical origin, ignoring paths and the default port.</summary>
    public static string? HttpOrigin(Uri server) => server.Scheme == "http" ? server.GetLeftPart(UriPartial.Authority).ToLowerInvariant() : null;

    public static string? OriginDigest(Uri server) => HttpOrigin(server) is { } origin
        ? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(origin))) : null;

    private static int? ParsePort(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            throw new ArgumentException("Port must be a number between 1 and 65535.");
        }
        return port;
    }

    private static Uri Build(string scheme, Uri parsed, int? port) => new UriBuilder(scheme, parsed.Host, port ?? -1, parsed.AbsolutePath.TrimEnd('/')).Uri;
}
