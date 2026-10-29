namespace Bale.Core.Connection;

/// <summary>Turns what a user typed into a Silo server base address.</summary>
public static class ServerAddress
{
    /// <summary>
    /// Accepts "silo.example.com", "https://silo.example.com/" or "http://192.168.1.10:8080".
    /// A missing scheme means https. The result never ends in a slash.
    /// </summary>
    public static bool TryParse(string? input, out Uri address)
    {
        address = null!;
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (!text!.Contains("://"))
        {
            text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp)
            || string.IsNullOrEmpty(parsed.Host))
        {
            return false;
        }

        var path = parsed.AbsolutePath.TrimEnd('/');
        address = new Uri(parsed.GetLeftPart(UriPartial.Authority) + path);
        return true;
    }

    /// <summary>The Kiota base URL form: no trailing slash.</summary>
    public static string ToBaseUrl(Uri address) => address.ToString().TrimEnd('/');
}
