using Bale.Core.Api.Models;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media.Imaging;

namespace Bale.Xbox.Views;

public sealed partial class ProfileView
{
    public ProfileView(Profile profile, Uri? avatarUrl)
    {
        Profile = profile;
        Initial = string.IsNullOrEmpty(profile.Name) ? "?" : profile.Name!.Substring(0, 1).ToUpperInvariant();
        LockVisibility = profile.HasPin == true ? Visibility.Visible : Visibility.Collapsed;
        if (avatarUrl is not null)
        {
            Avatar = new BitmapImage { DecodePixelType = DecodePixelType.Logical, DecodePixelWidth = 56, UriSource = avatarUrl };
        }
    }

    public Profile Profile { get; }

    public string Name => Profile.Name ?? string.Empty;

    public string Initial { get; }

    public BitmapImage? Avatar { get; }

    public Visibility LockVisibility { get; }
}
