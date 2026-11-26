using System.Xml.Linq;

namespace Bale.Core.Tests;

/// <summary>
/// Settings › Playback › Episodes: Skip Intros and Skip Credits (TvSettingsScreen.kt:1403, :1408). The page compiles only
/// on Windows, so its layout and wiring are pinned by source here; the reads and writes are PlaybackSettingsFeedTests.
/// </summary>
public class SkipSettingsWiringTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, name)).ReplaceLineEndings("\n");

    private static XElement Named(XDocument document, string name) =>
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

    [Fact]
    public void SettingsOffersSkipIntrosAndSkipCreditsInTheEpisodesGroup()
    {
        var page = XDocument.Parse(Read("SettingsPage.xaml"));
        var group = Named(page, "AutoPlayButton").Parent!;
        var rows = group.Elements().Select(element => element.Attribute(Xaml + "Name")?.Value).Where(name => name is not null).ToList();
        // TvSettingsScreen.kt's Episodes order: Auto-Play (and its Use Profile Setting), then Skip Intros, Skip Credits and
        // Skip Credits' Use Profile Setting, shown only while this Xbox has its own value.
        Assert.Equal(["AutoPlayButton", "AutoPlayProfileButton", "AutoPlayDescription", "IntroSkipButton", "SkipCreditsButton",
            "SkipCreditsProfileButton"], rows);
        Assert.Equal("Collapsed", Named(page, "SkipCreditsProfileButton").Attribute("Visibility")?.Value);
        var picker = Named(page, "IntroSkipPicker");
        Assert.Equal("Collapsed", picker.Attribute("Visibility")?.Value);
        Assert.Equal(["profile", "never", "ask", "always"],
            picker.Descendants().Where(element => element.Name.LocalName == "Button").Select(element => element.Attribute("Tag")?.Value));

        var code = Read("SettingsPage.cs.txt");
        Assert.Contains("AutoPlayButton, AutoPlayProfileButton,\n                IntroSkipButton, SkipCreditsButton, SkipCreditsProfileButton,", code);
        InOrder(Section(code, "public bool OnBack()", "private void OnPreviewKeyDown("), "CloseBufferingPicker();", "CloseIntroSkipPicker();");
        InOrder(Section(code, "private async void OnIntroSkipOptionClick(", "private async void OnSkipCreditsClick("),
            "CloseIntroSkipPicker();", "client.Settings.UseProfileIntroSkipModeAsync()", "client.Settings.SetIntroSkipModeAsync(mode)");
        InOrder(Section(code, "private async void OnSkipCreditsProfileClick(", "private async Task ChangeSkipSettingAsync("),
            "FocusTarget(SkipCreditsButton);", "client.Settings.UseProfileAutoSkipCreditsAsync()");
    }
}
