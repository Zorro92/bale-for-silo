using System.Text.RegularExpressions;
using System.Xml.Linq;
using Bale.Core.Settings;

namespace Bale.Core.Tests;

/// <summary>
/// Settings' rail selection and focus scrolling (TvSettingsScreen.kt SettingsRail / SettingsDetailPane,
/// TvSettingsPickerSheet). The Xbox app compiles only on Windows, so the XAML and its wiring are pinned by source here;
/// the pivot itself is SettingsFocusScrollTests, the theme's rail rows SettingsChromeTests.
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

    [Fact]
    public void EveryPaneScrollsItsFocusedRowToThePivot()
    {
        var page = XDocument.Parse(Read("SettingsPage.xaml"));
        var content = Assert.Single(Named(page, "DetailScroller").Elements());
        Assert.Equal("DetailContent", content.Attribute(Xaml + "Name")?.Value);
        Assert.Equal("OnDetailFocus", content.Attribute("GotFocus")?.Value);
        // One handler serves General, Playback, Subtitles and Server: every pane is that content's child.
        Assert.Equal(["GeneralPane", "PlaybackPane", "SubtitlesPane", "ServerPane"],
            content.Elements().Select(pane => (string?)pane.Attribute(Xaml + "Name")));

        var code = Read("SettingsPage.cs.txt");
        Assert.Contains("SettingsScroll.OwnBringIntoView(DetailContent);", Section(code, "public SettingsPage()", "protected override void OnNavigatedTo("));
        InOrder(Section(code, "private void OnDetailFocus(", "public void RestoreFocus("),
            "FocusManager.GetFocusedElement() is not FrameworkElement row || !SettingsInput.IsWithin(row, DetailContent)",
            "!ReferenceEquals(FocusManager.GetFocusedElement(), row)", "SettingsScroll.Reveal(DetailScroller, DetailContent, row, animate: true)");
        // A new visit and a newly selected category open at the top; refocusing the selected category (Left or Back from
        // its rows) leaves the pane where it was, as Android TV's pane does.
        InOrder(Section(code, "protected override void OnNavigatedTo(", "var visible = SettingsCategories.Visible("),
            "if (e.NavigationMode != NavigationMode.Back)", "_newVisit = true;");
        InOrder(Section(code, "private void OnLoaded(", "private void DetachFocusBoundary("), "if (!_active) return;",
            "if (_newVisit) DetailScroller.ChangeView(null, 0, null, disableAnimation: true);", "_newVisit = false;", "RestoreFocus();");
        InOrder(Section(code, "private void SelectCategory(", "private ToggleButton SelectedCategory"),
            "var changed = category != _category;", "_category = category;", "if (changed) DetailScroller.ChangeView(null, 0, null, disableAnimation: true);");
        Assert.Equal(2, Regex.Matches(code, @"DetailScroller\.ChangeView\(").Count);

        // The reveal is SettingsFocusScroll's pivot, and XAML's own bring-into-view never moves the list as well.
        var scroll = Read("SettingsScroll.cs.txt");
        Assert.Contains("content.BringIntoViewRequested += (_, args) => args.Handled = true;", scroll);
        InOrder(Section(scroll, "public static bool Reveal(", "catch (Exception error)"), "SettingsInput.IsWithin(row, content)",
            "row.TransformToVisual(content)", "SettingsFocusScroll.Target(top, row.ActualHeight, scroller.ViewportHeight, scroller.ScrollableHeight)",
            "scroller.ChangeView(null, offset, null, disableAnimation: !animate);");
    }

    [Fact]
    public void ThePickerSheetOpensOnTheCheckedRowAndFollowsFocus()
    {
        var sheet = XDocument.Parse(Read("SettingsPickerSheet.xaml"));
        Assert.Equal("Scroller", Named(sheet, "Options").Parent!.Attribute(Xaml + "Name")?.Value);
        var row = sheet.Descendants(Ui + "DataTemplate").Single().Element(Ui + "Button")!;
        Assert.Equal("OnOptionFocus", row.Attribute("GotFocus")?.Value);

        var code = Read("SettingsPickerSheet.cs.txt");
        Assert.Contains("SettingsScroll.OwnBringIntoView(Options);", Section(code, "public SettingsPickerSheet()", "public bool IsOpen"));
        // TvSettingsPickerSheet: scrollToItem(selected) before the first focus, so the first reveal after Show jumps.
        InOrder(Section(code, "public void Show(", "public void Hide()"), "Options.ItemsSource = _options;",
            "Scroller.ChangeView(null, 0, null, disableAnimation: true);", "_opening = true;", "FocusRow(_focusIndex, FocusState.Programmatic)");
        // Every later focus (Move's Up/Down) glides; a row reveals only while it keeps focus.
        InOrder(Section(code, "private void OnOptionFocus(", "private void OnOptionClick("), "_focusIndex = view.Index;", "var animate = !_opening;",
            "_opening = false;", "!ReferenceEquals(_options.ElementAtOrDefault(_focusIndex), view)", "SettingsScroll.Reveal(Scroller, Options, row, animate)");
        Assert.Contains("FocusRow(Math.Clamp(_focusIndex + step, 0, _options.Count - 1), FocusState.Keyboard);", code);
    }
}
