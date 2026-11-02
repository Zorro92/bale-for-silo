using System.Globalization;
using System.Xml.Linq;

namespace Bale.Core.Tests;

/// <summary>Source-level parity guards, not a substitute for Xbox rendering/focus verification.</summary>
public class SkylineChromeTests
{
    private static readonly XNamespace Ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XDocument Theme = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Skyline.xaml"));

    [Fact]
    public void ServerFieldFocusLiftStaysInsideTheScrollViewportWithoutMovingItsRestingEdge()
    {
        // TvMarqueeField.kt: a centered 1.04 scale needs 7.6dp to the left of the 380dp field.
        var scroll = Style("SkylineServerEntryScrollStyle");
        var content = Style("SkylineServerEntryStyle");
        var field = Style("SkylineServerAddressStyle");
        var focus = Named(field, "Focused");
        var padding = Numbers(Value(content, "Padding"));
        var margin = Numbers(Value(scroll, "Margin"));
        var width = Number(Value(field, "Width"));
        var height = Number(Value(field, "Height"));
        var scaleX = Number(StateValue(focus, "FieldLift.ScaleX"));
        var scaleY = Number(StateValue(focus, "FieldLift.ScaleY"));

        Assert.Equal("0", Value(scroll, "Padding"));
        Assert.Equal(1.04, scaleX);
        Assert.Equal(1.04, scaleY);
        Assert.Equal(0, margin[0] + padding[0]);
        Assert.Equal(450, Number(Value(content, "Width")) - padding[0] - padding[2]);
        Assert.True(padding[0] >= width * (scaleX - 1) / 2);
        Assert.True(Number(Value(scroll, "Width")) - padding[0] - width >= width * (scaleX - 1) / 2);
        Assert.True(padding[1] >= height * (scaleY - 1) / 2);
        Assert.True(padding[3] >= height * (scaleY - 1) / 2);
    }

    [Fact]
    public void ServerFieldUsesTheSameFourRoundedCornersForRestFocusAndError()
    {
        // TvMarqueeField.kt / TvMarqueeMetrics: 8dp corners, 42dp high, 15sp × 0.86 text.
        var field = Style("SkylineServerAddressStyle");
        Assert.Equal("8", Value(field, "CornerRadius"));
        Assert.Equal("42", Value(field, "Height"));
        Assert.Equal("12.9", Value(field, "FontSize"));
        foreach (var name in new[] { "Field", "FocusedField", "FieldError" })
        {
            Assert.Equal("{TemplateBinding CornerRadius}", (string?)Named(field, name).Attribute("CornerRadius"));
        }

        // The leading icon and editing column must not paint square backgrounds over the field.
        Assert.All(Named(field, "FieldContent").DescendantsAndSelf(), element => Assert.Null(element.Attribute("Background")));
        Assert.Equal("1", StateValue(Named(field, "Focused"), "FocusedField.Opacity"));
        Assert.Equal("1", StateValue(Named(field, "Invalid"), "FieldError.Opacity"));
        Assert.Equal("#0FFFFFFF", StateValue(Named(field, "Invalid"), "Field.Background"));
        Assert.Equal("Transparent", StateValue(Named(field, "Invalid"), "Field.BorderBrush"));
        Assert.Equal("1.5", (string?)Named(field, "FieldError").Attribute("BorderThickness"));
        Assert.Equal("#BFFF6961", (string?)Named(field, "FieldError").Attribute("BorderBrush"));
        Assert.DoesNotContain(Named(field, "Focused").Descendants(Ui + "Setter"),
            setter => (string?)setter.Attribute("Target") == "FieldError.Opacity");
    }

    [Fact]
    public void TopMenuTabsKeepOfficialWeightsAlphaAndSelectedBorderWithStringPresenters()
    {
        // TvTopMenuBar.kt: Medium at 62%, SemiBold selected/focused; 1dp selected border, none focused.
        var tab = Style("SkylineTabStyle");
        Assert.Equal("Medium", Value(tab, "FontWeight"));
        Assert.Equal("{StaticResource SkylineTabResting}", Value(tab, "Foreground"));
        Assert.Equal("#9EEDEDED", (string?)Theme.Root!.Elements(Ui + "SolidColorBrush")
            .Single(element => (string?)element.Attribute(Xaml + "Key") == "SkylineTabResting").Attribute("Color"));
        Assert.Equal("1", (string?)Named(tab, "SelectedFill").Attribute("BorderThickness"));
        foreach (var state in new[] { "Checked", "CheckedPointerOver", "CheckedPressed" })
        {
            Assert.Equal("SemiBold", StateValue(Named(tab, state), "Content.FontWeight"));
            Assert.Equal("{StaticResource SkylinePrimary}", StateValue(Named(tab, state), "Content.Foreground"));
        }
        foreach (var state in new[] { "Focused", "PointerFocused" })
        {
            Assert.Equal("Collapsed", StateValue(Named(tab, state), "SelectedFill.Visibility"));
            Assert.Equal("1", StateValue(Named(tab, state), "FocusedContent.Opacity"));
            Assert.Equal("0", StateValue(Named(tab, state), "Content.Opacity"));
        }
        Assert.Equal("SemiBold", (string?)Named(tab, "FocusedContent").Attribute("FontWeight"));
        Assert.All(new[] { "Content", "FocusedContent" }, name =>
            Assert.Equal("{TemplateBinding Content}", (string?)Named(tab, name).Attribute("Content")));
    }

    [Fact]
    public void TopMenuSearchUsesA19DpMaterialVectorRatherThanAFontEm()
    {
        // TvTopMenuBar.kt TvTopMenuIconButton: a 19dp viewport inside a 28dp circle.
        var search = Style("SkylineBarIconStyle");
        var viewport = search.Descendants(Ui + "Viewbox").Single();
        var glyph = Named(search, "Glyph");
        Assert.Equal("28", Value(search, "Width"));
        Assert.Equal("28", Value(search, "Height"));
        Assert.Equal("19", (string?)viewport.Attribute("Width"));
        Assert.Equal("19", (string?)viewport.Attribute("Height"));
        Assert.Equal(Ui + "Path", glyph.Name);
        Assert.Equal("24", (string?)glyph.Attribute("Width"));
        Assert.Equal("24", (string?)glyph.Attribute("Height"));
        Assert.Equal("{TemplateBinding Foreground}", (string?)glyph.Attribute("Fill"));
        Assert.Equal("{StaticResource SkylineTabResting}", Value(search, "Foreground"));
        foreach (var state in new[] { "Focused", "PointerFocused" })
        {
            Assert.Equal("{StaticResource SkylinePrimary}", StateValue(Named(search, state), "Disc.Fill"));
            Assert.Equal("{StaticResource SkylineBackground}", StateValue(Named(search, state), "Glyph.Fill"));
        }
    }

    private static XElement Style(string key) => Theme.Root!.Elements(Ui + "Style")
        .Single(element => (string?)element.Attribute(Xaml + "Key") == key);

    private static XElement Named(XElement parent, string name) => parent.Descendants()
        .Single(element => (string?)element.Attribute(Xaml + "Name") == name);

    private static string Value(XElement style, string property) => (string)style.Elements(Ui + "Setter")
        .Single(setter => (string?)setter.Attribute("Property") == property).Attribute("Value")!;

    private static string StateValue(XElement state, string target) => (string)state.Descendants(Ui + "Setter")
        .Single(setter => (string?)setter.Attribute("Target") == target).Attribute("Value")!;

    private static double Number(string value) => double.Parse(value, CultureInfo.InvariantCulture);

    private static double[] Numbers(string value) => value.Split(',').Select(Number).ToArray();
}
