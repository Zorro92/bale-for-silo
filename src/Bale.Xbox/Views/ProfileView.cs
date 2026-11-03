using Bale.Core.Api.Models;
using Bale.Core.Auth;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace Bale.Xbox.Views;

public sealed partial class ProfileView
{
    public ProfileView(Profile profile, Uri? avatarUrl, bool managing = false, bool isAdd = false)
    {
        Profile = profile;
        IsManaging = managing;
        IsAdd = isAdd;
        AvatarUrl = avatarUrl;
        Initial = string.IsNullOrEmpty(profile.Name) ? "?" : profile.Name!.Substring(0, 1).ToUpperInvariant();
        LockVisibility = profile.HasPin == true ? Visibility.Visible : Visibility.Collapsed;
        KidsVisibility = profile.IsChild == true ? Visibility.Visible : Visibility.Collapsed;
        Avatar = avatarUrl is null ? null : AvatarImage(avatarUrl, 110);
        // MarqueeProfileAvatar.kt: the ID-selected tint has a radial highlight/shade, not a flat fill.
        // Pre-rendered by scripts/generate-profile-art.py: UWP XAML has no RadialGradientBrush.
        AvatarFill = new ImageBrush
        {
            ImageSource = new BitmapImage(new Uri($"ms-appx:///Assets/ProfileAvatar-{ProfileTilePalette.Tint(profile.Id ?? string.Empty):X8}.png")),
            Stretch = Stretch.UniformToFill,
        };
    }

    public Profile Profile { get; }

    public Uri? AvatarUrl { get; }

    public bool IsManaging { get; }
    public bool IsAdd { get; }
    public string Name => IsAdd ? "Add profile" : Profile.Name ?? string.Empty;
    public double AvatarOpacity => IsManaging ? 0.6 : 1;
    public Visibility ProfileVisibility => IsAdd ? Visibility.Collapsed : Visibility.Visible;
    public Visibility AddVisibility => IsAdd ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ManageVisibility => IsManaging ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PencilVisibility => IsManaging && !IsAdd ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DeleteVisibility => IsManaging && !IsAdd && Core.Profiles.ProfileManagementGate.CanDelete(Profile) ? Visibility.Visible : Visibility.Collapsed;
    public string AccessibleName => IsAdd ? Name : string.Join(", ", new[] { Name,
        Profile.HasPin == true ? "PIN protected" : null, Profile.IsChild == true ? "Kids profile" : null,
        IsManaging ? "Edit" : null }.Where(value => value is not null));

    public string Initial { get; }

    public ImageSource? Avatar { get; }

    public ImageBrush AvatarFill { get; }

    public Visibility LockVisibility { get; }

    public Visibility KidsVisibility { get; }

    /// <summary>An image source for a profile picture: SVG through SvgImageSource, anything else decoded at the drawn size.</summary>
    public static ImageSource AvatarImage(Uri url, int size) =>
        ProfileAvatars.IsSvg(url)
            ? new SvgImageSource(url) { RasterizePixelWidth = size * 2, RasterizePixelHeight = size * 2 }
            : Services.Artwork.Load(url, width: size);
}
