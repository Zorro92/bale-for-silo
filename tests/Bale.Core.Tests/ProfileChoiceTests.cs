using Bale.Core.Api.Models;
using Bale.Core.Auth;

namespace Bale.Core.Tests;

public class ProfileChoiceTests
{
    private static Profile P(string id, bool pin = false) => new() { Id = id, Name = id, HasPin = pin };

    [Fact]
    public void ReusesRememberedUnlockedProfile() =>
        Assert.Equal("b", ProfileChoice.Automatic([P("a"), P("b")], "b")?.Id);

    [Fact]
    public void AsksAgainForRememberedLockedProfile() =>
        Assert.Null(ProfileChoice.Automatic([P("a"), P("b", pin: true)], "b"));

    [Fact]
    public void UsesLoneUnlockedProfile() =>
        Assert.Equal("a", ProfileChoice.Automatic([P("a")], null)?.Id);

    [Fact]
    public void AsksWhenLoneProfileIsLocked() =>
        Assert.Null(ProfileChoice.Automatic([P("a", pin: true)], null));

    [Fact]
    public void AsksWhenSeveralAndNoneRemembered() =>
        Assert.Null(ProfileChoice.Automatic([P("a"), P("b")], "gone"));
}
