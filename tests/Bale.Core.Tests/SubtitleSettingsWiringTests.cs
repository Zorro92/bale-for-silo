using System.Text.RegularExpressions;
using System.Xml.Linq;
using Bale.Core.Settings;

namespace Bale.Core.Tests;

/// <summary>
/// Settings › Subtitles (TvSettingsScreen.kt:1856–2023). The Xbox app compiles only on Windows, so the layout, the XAML's
/// resources and handlers and the Auto callers are pinned by source here; the reads, writes and mappings are
/// SubtitleSettingsFeedTests and SubtitleAppearanceTests, the renderer's side SubtitleTextStyleWiringTests.
/// </summary>
public class SubtitleSettingsWiringTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, name)).ReplaceLineEndings("\n");

    private static XElement Named(XContainer document, string name) =>
        document.Descendants().Single(element => element.Attribute(Xaml + "Name")?.Value == name);

    private static string Section(string text, string from, string to)
    {
        var start = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, from);
        var end = text.IndexOf(to, start, StringComparison.Ordinal);
        Assert.True(end > start, to);
        return text[start..end];
    }

    private static void InOrder(string text, params string[] parts)
    {
        var at = -1;
        foreach (var part in parts)
        {
            var next = text.IndexOf(part, at + 1, StringComparison.Ordinal);
            Assert.True(next > at, "out of order or missing: " + part);
            at = next;
        }
    }

    private static readonly string[] Rows =
    [
        "SubtitleLanguageButton", "SubtitleModeButton", "ForcedSubtitlesButton", "MatchDeviceButton", "DeviceOnlyButton", "FontSizeButton",
        "FontFamilyButton", "FontColorButton", "TextOpacityButton", "TextOutlineButton", "OutlineColorButton", "BackgroundStyleButton",
        "BackgroundOpacityButton", "BackgroundColorButton", "PositionButton", "ResetAppearanceButton",
    ];

    [Theory]
    [InlineData("SettingsPage.xaml", "SettingsPage.cs.txt")]
    [InlineData("SettingsPickerSheet.xaml", "SettingsPickerSheet.cs.txt")]
    [InlineData("SettingsConfirmDialog.xaml", "SettingsConfirmDialog.cs.txt")]
    public void EveryResourceAndHandlerTheXamlNamesExists(string xaml, string code)
    {
        var page = XDocument.Parse(Read(xaml));
        var keys = XDocument.Parse(Read("Skyline.xaml")).Root!.Elements().Select(element => (string?)element.Attribute(Xaml + "Key"))
            .OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var attribute in page.Descendants().Attributes())
        {
            foreach (Match match in Regex.Matches(attribute.Value, @"\{StaticResource ([A-Za-z0-9]+)\}")) Assert.Contains(match.Groups[1].Value, keys);
        }
        var source = Read(code);
        string[] events = ["Click", "Loaded", "GotFocus", "PreviewKeyDown", "PreviewKeyUp", "Tapped"];
        foreach (var attribute in page.Descendants().Attributes().Where(attribute => events.Contains(attribute.Name.LocalName)))
            Assert.Matches(new Regex(@"\bvoid " + Regex.Escape(attribute.Value) + @"\("), source);
    }

    [Fact]
    public void TheSubtitlesCategorySitsBetweenPlaybackAndServer()
    {
        var page = XDocument.Parse(Read("SettingsPage.xaml"));
        var rail = Named(page, "GeneralCategory").Parent!.Elements().Select(element => (string?)element.Attribute(Xaml + "Name")).ToList();
        Assert.Equal(["GeneralCategory", "PlaybackCategory", "SubtitlesCategory", "ServerCategory"], rail);
        var category = Named(page, "SubtitlesCategory");
        // TvSettingsCategory.Subtitles: settings_subtitles and settings_rail_subtitles; the ClosedCaption glyph.
        Assert.Equal("Subtitles", category.Attribute("Content")?.Value);
        Assert.Equal("Language and appearance", category.Attribute("Tag")?.Value);
        Assert.Equal("Collapsed", Named(page, "SubtitlesHeaderIcon").Attribute("Visibility")?.Value);

        var theme = XDocument.Parse(Read("Skyline.xaml"));
        var style = theme.Root!.Elements().Single(element => (string?)element.Attribute(Xaml + "Key") == "SkylineSettingsCategoryStyle");
        Assert.Equal(Named(page, "SubtitlesHeaderIcon").Attribute("Data")?.Value, Named(style, "SubtitlesGlyph").Attribute("Data")?.Value);
        Assert.Contains(Named(style, "SubtitlesIcon").Descendants(Ui + "Setter"), setter => setter.Attribute("Target")?.Value == "SubtitlesGlyph.Visibility");
        Assert.Contains(Named(style, "Focused").Descendants(Ui + "Setter"),
            setter => setter.Attribute("Target")?.Value == "SubtitlesGlyph.Fill" && setter.Attribute("Value")?.Value == "Black");

        var code = Read("SettingsPage.cs.txt");
        Assert.Contains("VisualStateManager.GoToState(SubtitlesCategory, \"SubtitlesIcon\", false);", code);
        Assert.Contains("{ AccountButton, GeneralCategory, PlaybackCategory, SubtitlesCategory, ServerCategory, SignOutButton }", code);
        // settings_blurb_subtitles.
        Assert.Contains("SettingsCategory.Subtitles => (\"Subtitles\", \"Language, behavior, and on-screen appearance.\"),", code);
        Assert.Contains("SubtitlesCategory.Visibility = visible.Contains(SettingsCategory.Subtitles)", code);
    }

    [Fact]
    public void ThePaneHasAndroidsRowsInOrder()
    {
        var page = XDocument.Parse(Read("SettingsPage.xaml"));
        var pane = Named(page, "SubtitlesPane");
        Assert.Equal("Collapsed", pane.Attribute("Visibility")?.Value);
        var buttons = pane.Descendants(Ui + "Button").Select(element => (string?)element.Attribute(Xaml + "Name")).ToList();
        Assert.Equal(Rows, buttons);
        Assert.Equal(["PROFILE", "APPEARANCE"], pane.Elements().SelectMany(group => group.Elements(Ui + "TextBlock").Take(1))
            .Select(header => header.Attribute("Text")?.Value));

        // Each picker row's Tag names a Bale.Core SubtitlePicker once, and its label is that sheet's title.
        var pickers = new List<SubtitlePicker>();
        foreach (var button in pane.Descendants(Ui + "Button").Where(element => element.Attribute("Tag") is not null))
        {
            Assert.True(Enum.TryParse<SubtitlePicker>(button.Attribute("Tag")!.Value, out var picker), button.Attribute("Tag")!.Value);
            Assert.Equal("OnSubtitlePickerClick", button.Attribute("Click")?.Value);
            Assert.Equal(SubtitleSettingsPickers.Title(picker), button.Descendants(Ui + "TextBlock").First().Attribute("Text")?.Value);
            pickers.Add(picker);
        }
        Assert.Equal(Enum.GetValues<SubtitlePicker>(), pickers.Order());

        // Text Opacity shows once the server's contract knows it; Reset Appearance is the destructive row; the appearance
        // rows sit in Android's nested group, after the preview and the two switches.
        Assert.Equal("Collapsed", Named(pane, "TextOpacityButton").Attribute("Visibility")?.Value);
        Assert.Equal("{StaticResource SkylineSettingsDestructiveRowStyle}", Named(pane, "ResetAppearanceButton").Attribute("Style")?.Value);
        var nested = Named(pane, "FontSizeButton").Parent!.Parent!;
        Assert.Equal("{StaticResource SkylineSettingsNestedGroupStyle}", nested.Attribute("Style")?.Value);
        Assert.Equal(Rows[5..], nested.Descendants(Ui + "Button").Select(element => (string?)element.Attribute(Xaml + "Name")));
        var appearance = Named(pane, "SubtitlePreview").Parent!.Elements().Select(element => (string?)element.Attribute(Xaml + "Name") ?? element.Name.LocalName).ToList();
        InOrder(string.Join(",", appearance), "SubtitlePreview", "MatchDeviceButton", "DeviceOnlyButton", "Border");
        var preview = Named(pane, "SubtitlePreview");
        Assert.Equal(5, preview.Descendants(Ui + "TextBlock").Count(element => element.Attribute("Text")?.Value == "Subtitles will look like this"));

        var code = Read("SettingsPage.cs.txt");
        InOrder(Section(code, "SettingsCategory.Subtitles => new Control[]", "SettingsCategory.Server =>"), Rows);
        // TvSubtitleSettingsPane's footer under the groups: settings_subtitles_scopeDevice / _scopeProfile.
        Assert.Contains("SubtitleScopeFooter.Text = setting.DeviceOverride ? \"Only on this device.\" : \"Saved for this profile on all devices.\";", code);
    }

    [Fact]
    public void ThePaneWritesWhatAndroidWrites()
    {
        var code = Read("SettingsPage.cs.txt");
        InOrder(Section(code, "private async void OnSubtitleOptionChosen(", "private async void OnForcedSubtitlesClick("),
            "CloseSubtitlePicker();", "settings.SetLanguageAsync(id)", "settings.SetModeAsync(mode)",
            "SubtitleSettingsPickers.Apply(picker, id, settings.Appearance.Value)", "settings.SetAppearanceAsync(next)");
        Assert.Contains("settings.SetShowForcedAsync(!settings.Preferences.ShowForced)", code);
        Assert.Contains("settings.SetMatchDevice(!settings.MatchDevice)", code);
        Assert.Contains("settings.SetAppearanceOnlyOnThisDeviceAsync(!settings.Appearance.DeviceOverride)", code);
        Assert.Contains("settings.SetAppearanceAsync(appearance with { TextOutline = !appearance.TextOutline })", code);
        InOrder(Section(code, "private async void OnResetAppearanceConfirmed(", "private async Task ChangeSubtitleSettingAsync("),
            "FocusTarget(ResetAppearanceButton);", "client.SubtitleSettings.SetAppearanceAsync(SubtitleAppearance.Default)");
        // TvSettingsConfirmDialog: settings_subtitles_resetTitle / _resetMessage / _resetConfirm.
        Assert.Contains("ResetPrompt.Show(\"Reset subtitle appearance?\", \"This restores every subtitle appearance option to its default.\", \"Reset\");", code);
        // Back closes a sheet or the dialog before it moves focus; keys and focus stay inside them.
        InOrder(Section(code, "public bool OnBack()", "private void OnPreviewKeyDown("), "SignOutPrompt.Dismiss()", "ResetPrompt.Dismiss()",
            "OptionSheet.IsOpen", "CloseSubtitlePicker();", "BufferingPicker");
        InOrder(Section(code, "private void OnPreviewKeyDown(", "private void OnPreviewKeyUp("), "ResetPrompt.IsOpen) return;",
            "if (OptionSheet.IsOpen)", "OptionSheet.Move(vertical);");
        InOrder(Section(code, "private void OnGettingFocus(", "private void OnAccountClick("), "OptionSheet.IsOpen", "OptionSheet.FocusTarget");
        Assert.Contains("OptionSheet.Hide();", Section(code, "protected override void OnNavigatedFrom(", "private void OnLoaded("));
        Assert.Contains("_ = ReadSubtitlesAsync(client, _profileId);", code);
    }

    [Fact]
    public void ThePickerSheetAndDialogAreTheTvSheets()
    {
        var sheet = XDocument.Parse(Read("SettingsPickerSheet.xaml"));
        Assert.Equal("{StaticResource SkylineSettingsPickerCardStyle}", sheet.Descendants(Ui + "Border").First().Attribute("Style")?.Value);
        Assert.Equal("Collapsed", Named(sheet, "ModalLayer").Attribute("Visibility")?.Value);
        // The rows scroll inside the card; the header does not.
        Assert.Equal("ScrollViewer", Named(sheet, "Options").Parent!.Name.LocalName);
        var template = sheet.Descendants(Ui + "DataTemplate").Single();
        Assert.Equal("views:SettingsPickerOptionView", template.Attribute(Xaml + "DataType")?.Value);
        Assert.Equal("{StaticResource SkylineSettingsPickerOptionStyle}", template.Element(Ui + "Button")?.Attribute("Style")?.Value);
        var sheetCode = Read("SettingsPickerSheet.cs.txt");
        Assert.Contains("VisualStateManager.GoToState(row, view.Selected ? \"Selected\" : \"Unselected\", false);", sheetCode);

        var dialog = XDocument.Parse(Read("SettingsConfirmDialog.xaml"));
        Assert.Equal(["CancelButton", "ConfirmButton"], Named(dialog, "Actions").Elements(Ui + "Button").Select(element => (string?)element.Attribute(Xaml + "Name")));
        Assert.Equal("{StaticResource SkylineDialogDestructiveButtonStyle}", Named(dialog, "ConfirmButton").Attribute("Style")?.Value);
        // SurfaceShadow finds these by name.
        Assert.Equal("Rectangle", Named(dialog, "ShadowMask").Name.LocalName);
        Assert.Equal("ShadowHost", Named(dialog, "ShadowMask").Parent!.Attribute(Xaml + "Name")?.Value);
        Assert.Contains("CancelButton.Focus(FocusState.Programmatic)", Read("SettingsConfirmDialog.cs.txt"));
        var theme = XDocument.Parse(Read("Skyline.xaml"));
        var card = theme.Root!.Elements().Single(element => (string?)element.Attribute(Xaml + "Key") == "SkylineDialogCardStyle");
        Assert.Equal("470", card.Elements(Ui + "Setter").Single(setter => setter.Attribute("Property")?.Value == "Width").Attribute("Value")?.Value);
    }

    [Fact]
    public void DetailAndUpNextStartAutoFromTheSubtitleSettings()
    {
        var detail = Read("DetailPage.AutomaticAudio.cs.txt");
        InOrder(detail, "client.SubtitleSettings.ReadPreferencesAsync()", "client.GetProfilesAsync()",
            "_subtitleAutoContext = SubtitlePreferences.AutoContextFor(resolved, profile);");
        Assert.DoesNotContain("new(profile.SubtitleLanguage", detail);
        var upNext = Section(Read("PlayerPage.UpNext.cs.txt"), "private static async Task<PlaybackSelection> NextEpisodeSelectionAsync(",
            "private async Task AdvanceToNextAsync(");
        InOrder(upNext, "attempt.Client.SubtitleSettings.ReadPreferencesAsync()", "attempt.Client.GetProfilesAsync()",
            "SubtitlePreferences.AutoContextFor(await preferences, profile)");
        Assert.DoesNotContain("new(profile.SubtitleLanguage", upNext);
    }

    [Fact]
    public void TheConsoleCaptionReaderLogsNoText()
    {
        var captions = Read("SystemCaptions.cs.txt");
        Assert.DoesNotContain("ex.Message", captions);
        Assert.DoesNotContain("#if DEBUG_SERVER", captions);
    }
}
