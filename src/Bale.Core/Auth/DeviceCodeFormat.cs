namespace Bale.Core.Auth;

/// <summary>
/// How device sign-in codes are shown. The server issues eight digits ("4821-7730");
/// older servers issue eight letters and digits. Both are shown grouped 4+4.
/// Mirrors <c>DeviceCodeFormat</c> in Silo's Android client.
/// </summary>
public static class DeviceCodeFormat
{
    public const int Length = 8;

    /// <summary>"4821-7730" → "4821 7730". Anything that isn't eight characters is shown as sent, dashes as spaces.</summary>
    public static string Display(string userCode)
    {
        var compact = Normalize(userCode);
        return compact.Length == Length
            ? compact.Substring(0, 4) + " " + compact.Substring(4)
            : userCode.Trim().Replace('-', ' ');
    }

    /// <summary>Strips everything but letters and digits and uppercases, the form the server compares.</summary>
    public static string Normalize(string input) =>
        new string(input.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    /// <summary>
    /// The typed-URL form of the verification address: "silo.example.com/activate", without
    /// the https scheme (http keeps it, since the browser needs it).
    /// </summary>
    public static string ActivateText(string? verificationUri, string verificationUriComplete)
    {
        var raw = string.IsNullOrWhiteSpace(verificationUri)
            ? verificationUriComplete.Split('?')[0]
            : verificationUri!;
        var trimmed = raw.Trim().TrimEnd('/');
        return trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? trimmed.Substring("https://".Length)
            : trimmed;
    }
}
