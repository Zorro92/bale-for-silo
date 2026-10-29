using Bale.Core.Api.Models;
using Bale.Core.Auth;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace Bale.Xbox.Views;

public sealed partial class ProfileView
{
    public ProfileView(Profile profile, Uri? avatarUrl)
    {
        Profile = profile;
        AvatarUrl = avatarUrl;
        Initial = string.IsNullOrEmpty(profile.Name) ? "?" : profile.Name!.Substring(0, 1).ToUpperInvariant();
        LockVisibility = profile.HasPin == true ? Visibility.Visible : Visibility.Collapsed;
        Avatar = avatarUrl is null ? null : AvatarImage(avatarUrl, 136);
    }

    public Profile Profile { get; }

    public Uri? AvatarUrl { get; }

    public string Name => Profile.Name ?? string.Empty;

    public string Initial { get; }

    public ImageSource? Avatar { get; }

    public Visibility LockVisibility { get; }

    /// <summary>An image source for a profile picture: SVG through SvgImageSource, anything else decoded at the drawn size.</summary>
    public static ImageSource AvatarImage(Uri url, int size) =>
        ProfileAvatars.IsSvg(url)
            ? new SvgImageSource(url) { RasterizePixelWidth = size * 2, RasterizePixelHeight = size * 2 }
            : new BitmapImage { DecodePixelType = DecodePixelType.Logical, DecodePixelWidth = size, UriSource = url };
}
