using System.Text.RegularExpressions;
using System.Xml.Linq;
using Bale.Core.Settings;

namespace Bale.Core.Tests;

/// <summary>
/// Settings' rail selection (TvSettingsScreen.kt SettingsRail). The Xbox app compiles only on Windows, so the XAML and
/// its wiring are pinned by source here; the theme's rail rows are SettingsChromeTests.
/// </summary>
public class SettingsNavigationWiringTests
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

    [Fact]
    public void EveryCategoryRowIsInTheListThatChecksAndUnchecksIt()
    {
        var page = XDocument.Parse(Read("SettingsPage.xaml"));
        var toggles = Named(page, "CategoryRail").Descendants(Ui + "ToggleButton").ToList();
        Assert.NotEmpty(toggles);
        var rows = toggles.Select(toggle => toggle.Attribute(Xaml + "Name")!.Value).ToList();
        foreach (var toggle in toggles)
        {
            Assert.Equal("{StaticResource SkylineSettingsCategoryStyle}", toggle.Attribute("Style")?.Value);
            // The style gives every row the rail's width; a local width or alignment would undo it.
            Assert.Null(toggle.Attribute("Width"));
            Assert.Null(toggle.Attribute("HorizontalAlignment"));
        }

        // One list pairs each row with its category, in rail order, each once.
        var code = Read("SettingsPage.cs.txt");
        var list = Section(code, "private (SettingsCategory Category, ToggleButton Row)[] Categories =>", "];");
        var pairs = Regex.Matches(list, @"\(SettingsCategory\.(\w+), (\w+)\)").Select(match => (Category: match.Groups[1].Value, Row: match.Groups[2].Value)).ToList();
        Assert.Equal(rows, pairs.Select(pair => pair.Row));
        foreach (var (category, row) in pairs)
        {
            Assert.True(Enum.TryParse<SettingsCategory>(category, out _), category);
            Assert.Equal(category + "Category", row);
        }

        // Selecting a category checks its row and unchecks the others; Sign Out unchecks them all (TvSettingsScreen.kt
        // railActionHasFocus). Both go through the list, and nothing else sets a category row's IsChecked.
        Assert.Contains("foreach (var (each, row) in Categories) row.IsChecked = each == category;",
            Section(code, "private void SelectCategory(", "private ToggleButton SelectedCategory"));
        Assert.Contains("foreach (var (_, row) in Categories) row.IsChecked = false;", Section(code, "private void OnSignOutFocus(", "private void OnSignOutClick("));
        foreach (var row in rows) Assert.DoesNotMatch(new Regex(@"\b" + row + @"\.IsChecked\b"), code);
        Assert.Equal(2, Regex.Matches(code, @"\.IsChecked\s*=").Count);
    }
}
