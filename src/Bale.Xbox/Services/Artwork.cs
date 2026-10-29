using Windows.UI.Xaml.Media.Imaging;

namespace Bale.Xbox.Services;

/// <summary>
/// Every poster, still, logo and photo, decoded at the size it's drawn. Image hosts drop the odd
/// connection when a screen asks for dozens of images at once (seen as E_NETWORK_ERROR on the
/// console, and on curl from a PC); the official apps' HTTP stacks retry those, so this does too:
/// twice, after a short pause, before leaving the placeholder.
/// </summary>
internal static class Artwork
{
    private const int Retries = 2;

    public static BitmapImage Load(Uri uri, int? width = null, int? height = null)
    {
        var image = new BitmapImage { DecodePixelType = DecodePixelType.Logical };
        if (width is { } w)
        {
            image.DecodePixelWidth = w;
        }
        else if (height is { } h)
        {
            image.DecodePixelHeight = h;
        }

        var attempt = 0;
        image.ImageFailed += async (_, e) =>
        {
            try
            {
                if (++attempt > Retries)
                {
                    Log.Info($"Image failed ({uri.Host}{uri.AbsolutePath}): {e.ErrorMessage}");
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(700 * attempt));
                image.UriSource = null;
                image.UriSource = uri;
            }
            catch (Exception ex)
            {
                Log.Error("Image retry", ex);
            }
        };
        image.UriSource = uri;
        return image;
    }
}
