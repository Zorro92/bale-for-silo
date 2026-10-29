namespace Bale.Core.Auth;

/// <summary>Where to load a profile's picture from, in a form an image control can draw.</summary>
public static class ProfileAvatars
{
    /// <summary>
    /// Resolves a profile's avatar_url. Silo's preset avatars come from DiceBear as SVG; DiceBear
    /// serves the same image as PNG at the same path, which every image control can draw, so preset
    /// URLs are switched to PNG. Other URLs are returned resolved but unchanged.
    /// </summary>
    public static Uri? ImageUrl(string? avatarUrl, Func<string?, Uri?> resolve)
    {
        if (resolve(avatarUrl) is not { } url)
        {
            return null;
        }

        if (url.Host.EndsWith("dicebear.com", StringComparison.OrdinalIgnoreCase) && url.AbsolutePath.EndsWith("/svg", StringComparison.Ordinal))
        {
            var builder = new UriBuilder(url) { Path = url.AbsolutePath.Substring(0, url.AbsolutePath.Length - "svg".Length) + "png" };
            return builder.Uri;
        }

        return url;
    }

    /// <summary>True when the picture is SVG and needs an SVG-capable image source.</summary>
    public static bool IsSvg(Uri url) =>
        url.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) || url.AbsolutePath.EndsWith("/svg", StringComparison.Ordinal);
}
