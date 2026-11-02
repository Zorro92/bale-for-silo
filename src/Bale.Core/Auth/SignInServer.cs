using System.Globalization;

namespace Bale.Core.Auth;

/// <summary>SignInServer.kt: the public server name, then the saved name, then its host and port.</summary>
public sealed record SignInServer(Uri Server, string? SavedName = null, ServerBranding? Branding = null)
{
    public string Name => string.IsNullOrWhiteSpace(Branding?.ServerName)
        ? string.IsNullOrWhiteSpace(SavedName) ? Address : SavedName.Trim() : Branding.ServerName.Trim();

    public string Address => Server.Authority;

    public bool IsSecure => Server.Scheme == Uri.UriSchemeHttps;

    /// <summary>MarqueeServerMark.kt: the display name's uppercased first letter, never a generic server glyph.</summary>
    public string Initial => string.IsNullOrWhiteSpace(Name) ? string.Empty : StringInfo.GetNextTextElement(Name).ToUpperInvariant();

    public Uri? MarkUrl => Branding?.MarkUrl;

    public static async Task<string?> FetchNameAsync(Uri server, CancellationToken cancellationToken = default,
        HttpMessageHandler? transport = null) =>
        (await ServerBranding.FetchAsync(server, cancellationToken, transport).ConfigureAwait(false))?.ServerName;
}
