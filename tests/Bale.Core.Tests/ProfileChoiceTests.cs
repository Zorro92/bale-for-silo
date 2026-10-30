using Bale.Core.Api.Models;
using Bale.Core.Auth;

namespace Bale.Core.Tests;

public class ProfileChoiceTests
{
    private static Profile P(string id, bool pin = false) => new() { Id = id, Name = id, HasPin = pin };

    [Fact]
    public void DefaultAsksEvenWhenAProfileWasRemembered() =>
        Assert.Null(ProfileChoice.Automatic([P("a"), P("b")], "b"));

    [Fact]
    public void DefaultAsksEvenInASingleProfileHousehold() =>
        Assert.Null(ProfileChoice.Automatic([P("a")], null));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AutomaticReopensTheRememberedProfileIncludingPinProfiles(bool pin) =>
        Assert.Equal("b", ProfileChoice.Automatic([P("a"), P("b", pin)], "b", openAutomatically: true)?.Id);

    [Fact]
    public void AutomaticDoesNotChooseSomebodyElseWhenTheSavedProfileWasDeleted() =>
        Assert.Null(ProfileChoice.Automatic([P("a")], "gone", openAutomatically: true));

    [Fact]
    public void AutomaticWithoutARememberedProfileStillAsks() =>
        Assert.Null(ProfileChoice.Automatic([P("a")], null, openAutomatically: true));

    [Theory]
    [InlineData(null, true, null)]
    [InlineData("", true, null)]
    [InlineData(" ", true, null)]
    [InlineData("b", false, null)]
    [InlineData("b", true, "b")]
    public void StartupDecisionNeedsNoNetwork(string? rememberedId, bool automatic, string? expected) =>
        Assert.Equal(expected, ProfileChoice.AutomaticId(rememberedId, automatic));
}
