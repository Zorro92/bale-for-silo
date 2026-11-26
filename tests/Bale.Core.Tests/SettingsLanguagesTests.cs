using Bale.Core.Settings;

namespace Bale.Core.Tests;

/// <summary>Settings › Subtitles › Language (silo-android shared LanguageOptions.kt over the manifest's language floor).</summary>
public class SettingsLanguagesTests
{
    [Fact]
    public void TheFloorIsTheManifestsRevisionSevenSet()
    {
        // contracts/settings/v1/manifest.json playback_subtitle_languages, introduced_in <= 7 (LanguageOptions.FLOOR_REVISION).
        Assert.Equal(37, SettingsLanguages.SubtitleFloor.Count);
        Assert.Equal(["ar", "bn", "bg", "zh", "hr"], SettingsLanguages.SubtitleFloor.Take(5));
        Assert.DoesNotContain("pt-BR", SettingsLanguages.SubtitleFloor);
        Assert.DoesNotContain("zh-Hans", SettingsLanguages.SubtitleFloor);
        Assert.Equal(SettingsLanguages.SubtitleFloor.Count, SettingsLanguages.SubtitleFloor.Distinct().Count());
    }

    [Fact]
    public void NoneLeadsThenTheFloorSuggestionsAndTheCurrentValueEachOnce()
    {
        var options = SettingsLanguages.SubtitleOptions("eng", ["ja", "x-klingon", "Off", "pt-BR", "pt_BR", "und", " "]);
        Assert.Equal(new SettingsChoice("", "None"), options[0]);
        var ids = options.Select(option => option.Id).ToList();
        // A suggestion already in the floor is not repeated; one that is a new language or region is added once.
        Assert.Single(ids, id => id == "ja");
        Assert.Single(ids, id => id.Equals("pt-BR", StringComparison.OrdinalIgnoreCase) || id == "pt_BR");
        Assert.DoesNotContain("x-klingon", ids);
        Assert.DoesNotContain("Off", ids);
        Assert.DoesNotContain("und", ids);
        // The current value replaces its alias in the floor, so its exact spelling is the checked row.
        Assert.Contains("eng", ids);
        Assert.DoesNotContain("en", ids);
        Assert.Equal(1 + SettingsLanguages.SubtitleFloor.Count + 1, options.Count);
    }

    [Fact]
    public void LabelsAreEnglishLanguageNamesAndNoneForNoPreference()
    {
        Assert.Equal("None", SettingsLanguages.SubtitleLabel(null));
        Assert.Equal("None", SettingsLanguages.SubtitleLabel(""));
        Assert.Equal("None", SettingsLanguages.SubtitleLabel("Off"));
        Assert.Equal("English", SettingsLanguages.SubtitleLabel("en"));
        Assert.Equal("English", SettingsLanguages.SubtitleLabel("eng"));
        Assert.Equal("Japanese", SettingsLanguages.SubtitleLabel("ja"));
        Assert.Equal("Portuguese (Brazil)", SettingsLanguages.SubtitleLabel("pt-BR"));
        var labels = SettingsLanguages.SubtitleOptions(null, null).Skip(1).Select(option => option.Label).ToList();
        Assert.All(labels, label => Assert.False(string.IsNullOrWhiteSpace(label)));
        Assert.Equal(labels.Count, labels.Distinct().Count());
        Assert.Equal("Language", SettingsLanguages.SubtitleTitle);
    }

    [Fact]
    public void PreservableTagsAreLanguageTagsOnly()
    {
        Assert.True(SettingsLanguages.IsPreservable("en"));
        Assert.True(SettingsLanguages.IsPreservable("es-419"));
        Assert.True(SettingsLanguages.IsPreservable("zh_Hant"));
        Assert.False(SettingsLanguages.IsPreservable("Default"));
        Assert.False(SettingsLanguages.IsPreservable("English"));
        Assert.False(SettingsLanguages.IsPreservable("und"));
        Assert.False(SettingsLanguages.IsPreservable("e"));
        Assert.False(SettingsLanguages.IsPreservable(null));
    }
}
