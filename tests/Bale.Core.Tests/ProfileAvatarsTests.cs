using Bale.Core.Auth;

namespace Bale.Core.Tests;

public class ProfileAvatarsTests
{
    private static Uri? Resolve(string? url) =>
        string.IsNullOrEmpty(url) ? null : new Uri(new Uri("https://silo.example.com/"), url);

    [Fact]
    public void DiceBearPresetsSwitchToPng() =>
        Assert.Equal(
            "https://api.dicebear.com/9.x/fun-emoji/png?seed=sprout-sunset&size=128",
            ProfileAvatars.ImageUrl("https://api.dicebear.com/9.x/fun-emoji/svg?seed=sprout-sunset&size=128", Resolve)!.ToString());

    [Fact]
    public void UploadedAvatarsResolveAgainstTheServer() =>
        Assert.Equal("https://silo.example.com/api/v2/profiles/p1/avatar", ProfileAvatars.ImageUrl("/api/v2/profiles/p1/avatar", Resolve)!.ToString());

    [Fact]
    public void NoAvatarIsNull() => Assert.Null(ProfileAvatars.ImageUrl(null, Resolve));
}
