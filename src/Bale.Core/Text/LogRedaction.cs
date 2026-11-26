using System.Text.RegularExpressions;

namespace Bale.Core.Text;

/// <summary>
/// What bale.log may hold (the Xbox app's Services/Log.cs runs every line through here). Credentials never: token-style
/// query and JSON values, Authorization headers and Bearer tokens are masked in every build. Production builds also drop
/// what identifies a server or a title: absolute URLs keep only their scheme, query strings go, IPv4 addresses and
/// host:port pairs (socket error text) lose the host, and an API problem keeps its status and title but not the
/// server's detail. Debug-server builds keep those for diagnosis. AGENTS.md's rule stands: code logs only fixed
/// diagnostics, IDs and HRESULTs; this is the safety net for exception text and server answers.
/// </summary>
public static partial class LogRedaction
{
    public const string Redacted = "[redacted]";

    /// <summary>Every build: credentials masked, nothing else changed.</summary>
    public static string MaskCredentials(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (text.Contains('=', StringComparison.Ordinal))
            text = CredentialParameter().Replace(text, match => $"{match.Groups[1].Value}={Redacted}");
        if (text.Contains('"', StringComparison.Ordinal))
            text = CredentialJson().Replace(text, match => $"\"{match.Groups[1].Value}\":\"{Redacted}\"");
        if (text.Contains("uthorization", StringComparison.OrdinalIgnoreCase))
            text = AuthorizationHeader().Replace(text, match => $"{match.Groups[1].Value}{Redacted}");
        if (text.Contains("earer", StringComparison.OrdinalIgnoreCase))
            text = BearerToken().Replace(text, match => $"{match.Groups[1].Value} {Redacted}");
        return text;
    }

    /// <summary>Production builds: URLs and query strings first (whole), then <see cref="MaskCredentials"/> and addresses.</summary>
    public static string ForProduction(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (text.Contains("://", StringComparison.Ordinal))
            text = AbsoluteUrl().Replace(text, match => $"{match.Groups[1].Value}://{Redacted}");
        if (text.Contains('?', StringComparison.Ordinal))
            text = PathQuery().Replace(text, $"?{Redacted}");
        text = MaskCredentials(text);
        if (text.Contains('.', StringComparison.Ordinal)) text = Ipv4Address().Replace(text, Redacted);
        if (text.Contains(':', StringComparison.Ordinal))
            text = HostAndPort().Replace(text, match => $"{Redacted}:{match.Groups[1].Value}");
        return text;
    }

    /// <summary>
    /// The HTTP status of an API failure, which an exception's message lacks: a Silo problem's status and title, and its
    /// detail (server text) only when <paramref name="includeDetail"/>; a bare Kiota API failure's status; else empty.
    /// </summary>
    public static string Describe(Exception error, bool includeDetail) => error switch
    {
        Api.Models.Problem problem => includeDetail
            ? $"HTTP {problem.ResponseStatusCode} {problem.Title}: {problem.Detail} "
            : $"HTTP {problem.ResponseStatusCode} {problem.Title} ",
        Microsoft.Kiota.Abstractions.ApiException api => $"HTTP {api.ResponseStatusCode} ",
        _ => string.Empty,
    };

    // token=, access_token=, refresh_token=, id_token=, st= (Silo's stream grant), sig=, signature=, api_key=, apikey=,
    // password=, secret=: the value up to the next separator. Not key= alone: the input logs write key=GamepadB.
    [GeneratedRegex(@"(?<![A-Za-z0-9_])((?:[A-Za-z0-9_]*token|st|sig|signature|api_key|apikey|password|secret))=[^&\s""'<>;,)\]]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialParameter();

    [GeneratedRegex(@"""([A-Za-z0-9_]*token|password|secret)""\s*:\s*""[^""]*""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialJson();

    [GeneratedRegex(@"((?:proxy-)?authorization\s*[:=]\s*)(?:[A-Za-z]+\s+)?[^\s,;""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationHeader();

    [GeneratedRegex(@"\b(bearer)\s+(?!\[redacted\])[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerToken();

    [GeneratedRegex(@"\b([A-Za-z][A-Za-z0-9+.\-]*)://[^\s""'<>)\]]+", RegexOptions.CultureInvariant)]
    private static partial Regex AbsoluteUrl();

    [GeneratedRegex(@"(?<=/[^\s?""'<>]*)\?[^\s""'<>)\]]+", RegexOptions.CultureInvariant)]
    private static partial Regex PathQuery();

    // Octets 0-255 only, so four-part versions such as 0.1.812.0 stay.
    [GeneratedRegex(@"(?<![\d.])(?:(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(?![\d.])", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv4Address();

    [GeneratedRegex(@"(?<![A-Za-z0-9.\-])(?=[A-Za-z0-9.\-]*[A-Za-z])[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)+:(\d{1,5})\b", RegexOptions.CultureInvariant)]
    private static partial Regex HostAndPort();
}
