using Bale.Core.Api.Models;
using Bale.Core.Profiles;

namespace Bale.Core.Tests;

public class ProfileFormTests
{
    [Fact]
    public void CreateStartsWithTheOfficialStyleAndNoRestrictions()
    {
        var form = new ProfileForm();
        Assert.Equal("fun-emoji", form.AvatarStyleId);
        Assert.Equal("preset:dicebear:fun-emoji:pepper-pebble", form.EffectiveAvatar);
        Assert.False(form.Access.IsChild);
        Assert.Null(form.BuildCreate().IsChild);
        Assert.Null(form.BuildCreate().LibraryRestrictionsEnabled);
        Assert.Null(form.BuildCreate().AllowedLibraryIds);
        Assert.Null(form.BuildCreate().Pin);
    }

    [Theory]
    [InlineData("", "Profile name is required")]
    [InlineData("  ", "Profile name is required")]
    [InlineData("  ALICE ", "A profile with this name already exists")]
    [InlineData("Bob", null)]
    public void NameValidationMatchesTheOfficialTrimmedCaseInsensitiveRule(string name, string? error)
    {
        var form = new ProfileForm { Name = name };
        Assert.Equal(error, form.Validate([new Profile { Id = "1", Name = "Alice" }]));
    }

    [Fact]
    public void RenameExcludesItselfButNotOtherProfiles()
    {
        var profiles = new[] { new Profile { Id = "1", Name = "Alice" }, new Profile { Id = "2", Name = "Bob" } };
        var form = new ProfileForm(profiles[0]);
        Assert.Null(form.Validate(profiles));
        form.Name = "bob";
        Assert.Equal("A profile with this name already exists", form.Validate(profiles));
    }

    [Fact]
    public void CreatePinFiltersDigitsAndRequiresFour()
    {
        var form = new ProfileForm { Name = "New" };
        form.ConfirmPin("a12b3");
        Assert.True(form.PinEnabled);
        Assert.Equal("123", form.Pin);
        Assert.Equal("PIN must be 4 digits", form.Validate([]));
        form.ConfirmPin("a12b3456");
        Assert.Equal("1234", form.BuildCreate().Pin);
        Assert.Null(form.Validate([]));
        form.TogglePin(false);
        Assert.Equal("", form.Pin);
        Assert.Null(form.BuildCreate().Pin);
    }

    [Theory]
    [InlineData(true, true, "", null, false)]
    [InlineData(true, false, "", "", true)]
    [InlineData(false, false, "", null, false)]
    [InlineData(false, true, "", null, false)]
    [InlineData(true, true, "4321", "4321", false)]
    [InlineData(false, true, "1234", "1234", false)]
    public void EditPinDistinguishesSetClearAndOmit(bool loadedPin, bool enabled, string input, string? expected, bool clear)
    {
        var form = new ProfileForm(new Profile { Id = "1", Name = "Alice", HasPin = loadedPin });
        form.TogglePin(enabled);
        form.ConfirmPin(input);
        Assert.Null(form.Validate([]));
        Assert.Equal(expected, form.PinValue);
        Assert.Equal(clear, form.BuildUpdate().ClearPin);
        Assert.Equal(string.IsNullOrEmpty(expected) ? null : expected, form.BuildUpdate().Pin);
    }

    [Fact]
    public void EditingABlankPinDoesNotRemoveAnExistingPin()
    {
        var form = new ProfileForm(new Profile { Id = "1", Name = "Alice", HasPin = true });
        form.ConfirmPin("");
        Assert.True(form.PinEnabled);
        Assert.Null(form.PinValue);
        form.ConfirmPin("12");
        Assert.Equal("PIN must be 4 digits", form.Validate([]));
    }

    [Fact]
    public void StyleShuffleAndInitialsMatchTheOfficialEffectiveAvatar()
    {
        var form = new ProfileForm { Name = "  Alice & Bob " };
        form.SelectStyle("initials");
        Assert.Equal("preset:dicebear:initials:Alice & Bob", form.EffectiveAvatar);
        form.Name = " ";
        Assert.Equal("preset:dicebear:initials:You", form.EffectiveAvatar);
        form.SelectStyle("identicon");
        var first = form.EffectiveAvatar;
        form.SelectAvatar(ProfileAvatarPresets.Batch("identicon")[7]);
        Assert.NotEqual(first, form.EffectiveAvatar);
        form.Shuffle();
        Assert.Equal(1, form.AvatarBatch);
        Assert.Equal(ProfileAvatarPresets.Batch("identicon", 1)[0].Reference, form.EffectiveAvatar);
        form.SelectStyle("bottts-neutral");
        Assert.Equal(0, form.AvatarBatch);
        Assert.Null(form.SelectedAvatarSeed);
    }

    [Theory]
    [InlineData("upload:stored")]
    [InlineData("preset:legacy")]
    [InlineData("preset:dicebear:identicon:stored")]
    public void LoadedAvatarAndUrlSurviveUntilThePickerMoves(string reference)
    {
        var form = new ProfileForm(new Profile { Id = "1", Avatar = reference, AvatarUrl = "/stored.png" });
        Assert.Equal(reference, form.EffectiveAvatar);
        Assert.Equal("/stored.png", form.PreviewAvatarUrl);
        form.Shuffle();
        Assert.NotEqual(reference, form.EffectiveAvatar);
        Assert.Null(form.PreviewAvatarUrl);
    }

    [Theory]
    [InlineData("", "", 0xFF6F668Bu)]
    [InlineData("A", "b", 0xFF577B83u)]
    [InlineData("Å", "ab", 0xFF6B7C93u)]
    [InlineData("😀", "", 0xFF8A785Eu)]
    public void PreviewTintMatchesKotlinUtf16HashAndPalette(string name, string avatar, uint expected)
    {
        Assert.Equal(expected, ProfileAvatarPresets.PreviewTint(name, avatar));
    }

    [Fact]
    public void PresetContractUsesAllFiveStylesAndEighteenPngImages()
    {
        Assert.Equal(new[] { "identicon", "initials", "bottts-neutral", "fun-emoji", "pixel-art-neutral" }, ProfileAvatarPresets.Styles.Select(style => style.Id));
        var presets = ProfileAvatarPresets.Batch("fun-emoji");
        Assert.Equal(18, presets.Count);
        Assert.Equal(18, presets.Select(preset => preset.Seed).Distinct().Count());
        Assert.All(presets, preset => Assert.EndsWith("/png", preset.ImageUrl.AbsolutePath));
        Assert.Equal("https://api.dicebear.com/9.x/initials/png?seed=Alice%20%26%20Bob&size=256", new ProfileAvatarPresets.Preset("initials", "Alice & Bob").ImageUrl.AbsoluteUri);
        Assert.Equal(new ProfileAvatarPresets.Preset("fun-emoji", "a:b"), ProfileAvatarPresets.Parse(" PRESET:DICEBEAR:fun-emoji:a:b "));
        Assert.Null(ProfileAvatarPresets.Parse("preset:dicebear::"));
    }

    [Fact]
    public void ChildFillsUntouchedDefaultsAndTurningItOffClearsLimits()
    {
        var access = new ProfileAccessLimits().WithChild(true);
        Assert.True(access.IsChild);
        Assert.Equal("PG", access.MaxContentRating);
        Assert.True(access.LibraryRestrictionsEnabled);
        Assert.True(access.NeedsLibrary());
        Assert.False(access.WithLibrary("1", true).NeedsLibrary());
        var cleared = access.WithLibrary("1", true).WithChild(false);
        Assert.False(cleared.IsChild);
        Assert.Null(cleared.MaxContentRating);
        Assert.False(cleared.LibraryRestrictionsEnabled);
        Assert.Empty(cleared.AllowedLibraryIds);
    }

    [Fact]
    public void ChildLeavesExplicitAnyAndAllLibrariesAlone()
    {
        var access = new ProfileAccessLimits().WithRating(null).WithRestrictions(false).WithChild(true);
        Assert.Null(access.MaxContentRating);
        Assert.False(access.LibraryRestrictionsEnabled);
    }

    [Fact]
    public void UnchangedUnknownRatingAndDeletedLibrariesAreOmittedOnEdit()
    {
        var form = new ProfileForm(new Profile { Id = "1", Name = "Alice", MaxContentRating = "TV-Y7", LibraryRestrictionsEnabled = true, AllowedLibraryIds = ["gone"] });
        Assert.Null(form.Validate([], new HashSet<string> { "1" }));
        Assert.Null(form.BuildUpdate().AllowedLibraryIds);
        Assert.Null(form.BuildUpdate().MaxContentRating);
        Assert.Equal("TV-Y7", ProfileAccessLimits.RatingOptions(form.Access.MaxContentRating)[0]);
        form.Access = form.Access.WithLibrary("1", true);
        Assert.Equal(new[] { "1" }, form.BuildUpdate(new HashSet<string> { "1" }).AllowedLibraryIds);
        form.Access = form.Access.WithRating(null).WithRestrictions(false);
        Assert.True(form.BuildUpdate().ClearRating);
        Assert.False(form.BuildUpdate().LibraryRestrictionsEnabled);
        Assert.Empty(form.BuildUpdate().AllowedLibraryIds!);
    }

    [Fact]
    public void ChangedAllowlistMustIncludeAnAssignableLibrary()
    {
        var form = new ProfileForm { Name = "New", Access = new ProfileAccessLimits().WithRestrictions(true).WithLibrary("gone", true) };
        Assert.Null(form.Validate([])); // Unknown list is not an empty list.
        Assert.Equal("Select at least one library for this profile.", form.Validate([], new HashSet<string> { "1" }));
        form.Access = form.Access.WithLibrary("1", true);
        Assert.Null(form.Validate([], new HashSet<string> { "1" }));
    }

    [Fact]
    public void RatingLadderAndSubtitleOffKeepOfficialSemantics()
    {
        Assert.True(ProfileAccessLimits.UnderCeiling("PG", "R"));
        Assert.False(ProfileAccessLimits.UnderCeiling("R", "R"));
        Assert.True(ProfileAccessLimits.UnderCeiling("NC-17", null));
        Assert.False(ProfileAccessLimits.UnderCeiling("unknown", null));
        var form = new ProfileForm();
        form.SelectSubtitleMode("Off");
        Assert.Equal("off", form.BuildCreate().SubtitleMode);
        form.SelectQuality("4K");
        Assert.Equal("2160p", form.QualityPreference);
        form.SelectQuality("Original");
        Assert.Null(form.QualityPreference);
    }
}
