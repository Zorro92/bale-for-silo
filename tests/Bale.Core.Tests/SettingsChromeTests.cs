using System.Xml.Linq;

namespace Bale.Core.Tests;

/// <summary>Declarative Settings parity guards; these do not compile XAML or verify Xbox focus.</summary>
public class SettingsChromeTests
{
    private static readonly XNamespace Ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XDocument Theme = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Skyline.xaml"));

    [Fact]
    public void SplitPaneKeepsOfficialDpGeometryAndScaledTypography()
    {
        // TvSettingsScreen.kt: 44/40dp outer padding, 245dp rail, 26dp gap, 540dp detail rows.
        Assert.Equal("44,40,44,40", Value(Style("SkylineSettingsLayoutStyle"), "Padding"));
        Assert.Equal("245", Resource("SkylineSettingsRailWidth").Value);
        Assert.Equal("26,0,0,0", Resource("SkylineSettingsPaneInset").Value);
        Assert.Equal("540", Value(Style("SkylineSettingsGroupsStyle"), "Width"));
        Assert.Equal("Left", Value(Style("SkylineSettingsGroupsStyle"), "HorizontalAlignment"));
        Assert.Equal("50", Value(Style("SkylineSettingsCategoryStyle"), "Height"));
        Assert.Equal("38", Value(Style("SkylineSettingsValueRowStyle"), "Height"));
        Assert.Equal("20.64", Value(Style("SkylineSettingsTitleStyle"), "FontSize")); // 24sp × 0.86.
        Assert.Equal("18.06", Value(Style("SkylineSettingsPaneTitleStyle"), "FontSize")); // 21sp × 0.86.
        Assert.Equal("12.9", Value(Style("SkylineSettingsRowTextStyle"), "FontSize")); // 15sp × 0.86.
        Assert.Equal("12.04", Value(Style("SkylineSettingsSmallTextStyle"), "FontSize")); // 14sp × 0.86.
    }

    [Fact]
    public void CategorySelectionAndFocusUseSeparateFlatPlatters()
    {
        // TvSettingsCategoryRow: selected fill/border, then on-surface focus with dark content, no zoom.
        var category = Style("SkylineSettingsCategoryStyle");
        Assert.Equal("{StaticResource SkylineChromeSelected}", Attribute(Named(category, "SelectedFill"), "Background"));
        Assert.Equal("1", StateValue(Named(category, "Checked"), "SelectedFill.Opacity"));
        Assert.Equal("{StaticResource SkylinePrimary}", Attribute(Named(category, "FocusedFill"), "Background"));
        var focused = Named(category, "Focused");
        Assert.Equal("1", StateValue(focused, "FocusedFill.Opacity"));
        foreach (var target in new[] { "Title.Foreground", "Description.Foreground", "GeneralGlyph.Fill", "ServerGlyph.Fill" })
            Assert.Equal("Black", StateValue(focused, target));
        Assert.DoesNotContain(focused.Descendants(Ui + "Setter"), setter =>
            ((string?)setter.Attribute("Target"))?.Contains("Scale", StringComparison.Ordinal) == true);
        Assert.Equal("12.9", Attribute(Named(category, "Title"), "FontSize"));
        Assert.Equal("0.62", Attribute(Named(category, "Description"), "Opacity"));
    }

    [Fact]
    public void PolicyPickerShowsSelectionWithoutChangingDescriptionWeight()
    {
        // TvSettingsPickerSheet: 380dp/460dp card, semibold selected title and check, regular details.
        Assert.Equal("380", Value(Style("SkylineSettingsPickerCardStyle"), "Width"));
        Assert.Equal("460", Value(Style("SkylineSettingsPickerCardStyle"), "MaxHeight"));
        var option = Style("SkylineSettingsPickerOptionStyle");
        Assert.Equal("Normal", Attribute(Named(option, "Content"), "FontWeight"));
        Assert.Equal("SemiBold", StateValue(Named(option, "Selected"), "Content.FontWeight"));
        Assert.Equal("Visible", StateValue(Named(option, "Selected"), "Check.Visibility"));
        Assert.Equal("Normal", Value(Style("SkylineSettingsPickerDetailStyle"), "FontWeight"));
        Assert.Equal("Black", StateValue(Named(option, "Focused"), "Content.Foreground"));
    }

    [Fact]
    public void HomeEditorKeepsOfficialColumnAndCompactControls()
    {
        // TvHomeSectionsEditor.kt: 560dp column includes 36dp side padding; 38dp buttons scale 1.04.
        Assert.Equal("560", Value(Style("SkylineHomeSectionsEditorStyle"), "MaxWidth"));
        Assert.Equal("36,18", Value(Style("SkylineHomeSectionsEditorStyle"), "Padding"));
        Assert.Equal("18.92", Value(Style("SkylineHomeSectionsTitleStyle"), "FontSize")); // 22sp × 0.86.
        Assert.Equal("15.48", Value(Style("SkylineHomeSectionsToolbarTitleStyle"), "LineHeight"));
        Assert.Equal("14.62", Value(Style("SkylineHomeSectionsToolbarDescriptionStyle"), "LineHeight"));
        Assert.Equal("0.42", Value(Style("SkylineHiddenHomeSectionCaptionStyle"), "Opacity"));
        Assert.Equal("100", Value(Style("SkylineHomeSectionsMessageBoxStyle"), "Height"));
        Assert.Equal("Top", Value(Style("SkylineHomeSectionsMessageBoxStyle"), "VerticalAlignment"));
        Assert.Equal("38", Value(Style("SkylineHomeSectionsButtonStyle"), "Height"));
        var button = Style("SkylineHomeSectionIconButtonStyle");
        Assert.Equal("38", Value(button, "Width"));
        Assert.Equal("6", Attribute(Named(button, "Row"), "CornerRadius"));
        var viewport = button.Descendants(Ui + "Viewbox").Single();
        Assert.Equal("18", Attribute(viewport, "Width"));
        Assert.Equal("18", Attribute(viewport, "Height"));
        Assert.Equal("24", Attribute(Named(button, "Glyphs"), "Width"));
        Assert.Equal("0.28", StateValue(Named(button, "Disabled"), "Glyphs.Opacity"));
        Assert.Equal("1.04", StateValue(Named(button, "Focused"), "Lift.ScaleX"));
        Assert.Equal("1.04", StateValue(Named(button, "Focused"), "Lift.ScaleY"));
    }

    [Theory]
    [InlineData("SkylineSettingsValueRowStyle")]
    [InlineData("SkylineSettingsPickerOptionStyle")]
    [InlineData("SkylineHomeSectionsButtonStyle")]
    public void ElementContentHasOnlyOnePresenterAndNoImplicitTemplateTransitions(string key)
    {
        // AGENTS.md Native AOT rules: element content cannot have two parents; transitions crash.
        var style = Style(key);
        Assert.Single(style.Descendants(Ui + "ContentPresenter"));
        Assert.DoesNotContain(style.Descendants(), element =>
            element.Name.LocalName is "BrushTransition" or "ScalarTransition");
    }

    [Fact]
    public void EverySettingsAndHomeEditorStaticResourceExistsInSkyline()
    {
        var resources = Theme.Root!.Elements().Select(element => (string?)element.Attribute(Xaml + "Key"))
            .Where(key => key is not null).ToHashSet(StringComparer.Ordinal);
        var settings = Theme.Root.Elements().Where(element =>
        {
            var key = (string?)element.Attribute(Xaml + "Key");
            return key is not null && (key.StartsWith("SkylineSettings", StringComparison.Ordinal)
                || key.StartsWith("SkylineHomeSection", StringComparison.Ordinal)
                || key == "SkylineHiddenHomeSectionCaptionStyle");
        });
        foreach (var attribute in settings.SelectMany(element => element.DescendantsAndSelf().Attributes()))
        {
            const string prefix = "{StaticResource ";
            if (!attribute.Value.StartsWith(prefix, StringComparison.Ordinal)) continue;
            Assert.Contains(attribute.Value[prefix.Length..^1], resources);
        }
    }

    private static XElement Resource(string key) => Theme.Root!.Elements()
        .Single(element => (string?)element.Attribute(Xaml + "Key") == key);

    private static XElement Style(string key) => Resource(key);

    private static XElement Named(XElement parent, string name) => parent.Descendants()
        .Single(element => (string?)element.Attribute(Xaml + "Name") == name);

    private static string Attribute(XElement element, string name) => (string)element.Attribute(name)!;

    private static string Value(XElement style, string property) => Attribute(style.Elements(Ui + "Setter")
        .Single(setter => (string?)setter.Attribute("Property") == property), "Value");

    private static string StateValue(XElement state, string target) => Attribute(state.Descendants(Ui + "Setter")
        .Single(setter => (string?)setter.Attribute("Target") == target), "Value");
}
