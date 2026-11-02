using Bale.Core.Api.Models;
using Bale.Core.Settings;

namespace Bale.Core.Tests;

public class SettingsTests
{
    // Port of androidTvApp/.../settings/TvSettingsCategoryTest.kt, with explicit support so the
    // official eligibility rules remain tested even while Bale omits the unimplemented panes.
    [Fact]
    public void DiagnosticsSitsBetweenSubtitlesAndServer() => Assert.Equal(
        [SettingsCategory.General, SettingsCategory.Playback, SettingsCategory.Subtitles, SettingsCategory.Diagnostics, SettingsCategory.Server],
        SettingsCategories.Ordered);

    [Fact]
    public void AnIneligibleProfileHidesTheCategoryEntirely() => Assert.Equal(
        [SettingsCategory.General, SettingsCategory.Playback, SettingsCategory.Subtitles, SettingsCategory.Server],
        SettingsCategories.Visible(new SettingsSupport(PlayerAvailable: true, DiagnosticsAvailable: true)));

    [Fact]
    public void LosingEligibilityWhileShownFallsBackToGeneral() => Assert.Equal(SettingsCategory.General,
        SettingsCategories.Resolve(SettingsCategory.Diagnostics, new SettingsSupport(PlayerAvailable: true, DiagnosticsAvailable: true)));

    [Fact]
    public void AnUnrelatedCategoryIsNeverDisturbed()
    {
        Assert.Equal(SettingsCategory.Server, SettingsCategories.Resolve(SettingsCategory.Server, new SettingsSupport()));
        Assert.Equal(SettingsCategory.Diagnostics, SettingsCategories.Resolve(SettingsCategory.Diagnostics, new SettingsSupport(true, true, true)));
    }

    [Fact]
    public void BaleShipsOnlyPanesItCanHonorEvenForAnEligibleProfile()
    {
        Assert.Equal([SettingsCategory.General, SettingsCategory.Server], SettingsCategories.Visible(new SettingsSupport(DiagnosticsEligible: true)));
        Assert.Equal(SettingsCategory.General, SettingsCategories.Resolve(SettingsCategory.Playback, new SettingsSupport()));
        Assert.Equal(SettingsCategory.General, SettingsCategories.Resolve(SettingsCategory.Subtitles, new SettingsSupport()));
    }

    [Fact]
    public void AccountPayloadUsesProfileNameAndAdministratorRole()
    {
        var account = new Account { Username = "Household", Role = Account_role.Admin };
        Assert.Equal(new SettingsAccount("Viewer", "Administrator"), SettingsAccount.From(account, "Viewer"));
        account.Role = Account_role.User;
        Assert.Equal(new SettingsAccount("Viewer", "Household"), SettingsAccount.From(account, "Viewer"));
    }

    [Fact]
    public void AccountPayloadHasOfflineAndMissingIdentityFallbacks()
    {
        Assert.Equal(new SettingsAccount("Viewer", "Cached account"), SettingsAccount.From(null, "Viewer", "Cached account"));
        Assert.Equal(new SettingsAccount("Account", "Account"), SettingsAccount.From(new Account { Username = "Account" }, " "));
        Assert.Equal(new SettingsAccount("Silo", "Signed in"), SettingsAccount.From(null, null));
    }
}
