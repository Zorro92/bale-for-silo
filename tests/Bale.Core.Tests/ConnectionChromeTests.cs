using System.Xml.Linq;

namespace Bale.Core.Tests;

/// <summary>Declarative connection parity guards, not a Windows XAML build or Xbox focus verification.</summary>
public class ConnectionChromeTests
{
    private static readonly XNamespace Ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XDocument Theme = Document("Skyline.xaml");

    [Fact]
    public void ServerListKeepsOfficialSettingsGeometryAndFlatRows()
    {
        // TvServerListScreen.kt: 44/40dp outer inset, 48dp remove, 8dp gap, no focus zoom.
        Assert.Equal("44,40,44,40", Value(Style("SkylineServerListLayoutStyle"), "Margin"));
        Assert.Equal("48", Value(Style("SkylineSavedServerRemoveButtonStyle"), "Width"));
        Assert.Equal("48", Value(Style("SkylineSavedServerRemoveButtonStyle"), "Height"));
        Assert.Equal("8", Resource("SkylineSavedServerActionGap").Value);
        Assert.Equal("15.48", Value(Style("SkylineSavedServerNameStyle"), "FontSize"));
        Assert.Equal("13.76", Value(Style("SkylineSavedServerAddressStyle"), "FontSize"));
        var row = Style("SkylineSavedServerButtonStyle");
        Assert.Equal("8", Attribute(Named(row, "Surface"), "CornerRadius"));
        Assert.Equal("White", StateValue(Named(row, "Focused"), "Surface.Background"));
        Assert.Equal("Black", StateValue(Named(row, "Focused"), "Label.Foreground"));
        Assert.DoesNotContain(row.Descendants(), element => element.Name.LocalName == "ScaleTransform");
        Assert.Single(row.Descendants(Ui + "ContentPresenter"));
    }

    [Theory]
    [InlineData("SkylineCredentialFieldStyle", "TextBox")]
    [InlineData("SkylineCredentialPasswordStyle", "PasswordBox")]
    public void CredentialFieldsUseNativePartsAndIndependentErrorStroke(string key, string type)
    {
        // TvMarqueeField.kt: 42dp fields, 1.04 focus scale, 1.5dp error stroke even when focused.
        var style = Style(key);
        Assert.Equal(type, Attribute(style, "TargetType"));
        Assert.Equal("42", Value(style, "Height"));
        Assert.Equal("12.9", Value(style, "FontSize"));
        Assert.Equal("ContentElement", Attribute(Named(style, "ContentElement"), Xaml + "Name"));
        Assert.Equal("False", Attribute(Named(style, "ContentElement"), "IsTabStop"));
        Assert.Equal("False", Attribute(Named(style, "PlaceholderTextContentPresenter"), "IsTabStop"));
        Assert.Equal("1.5", Attribute(Named(style, "FieldError"), "BorderThickness"));
        Assert.Equal("1", StateValue(Named(style, "Invalid"), "FieldError.Opacity"));
        Assert.Equal("1.04", StateValue(Named(style, "Focused"), "FieldLift.ScaleX"));
        Assert.Equal("Black", StateValue(Named(style, "Focused"), "ContentElement.Foreground"));
        Assert.Equal("1", StateValue(Named(style, "Focused"), "FocusedField.Opacity"));
    }

    [Fact]
    public void PasswordRowKeepsOfficialWidthAndDoesNotUseImplicitReveal()
    {
        // TvLoginScreen.kt: the field + 8dp gap + reveal action occupy one 380dp row.
        Assert.Equal("380", Value(Style("SkylineCredentialFieldStyle"), "Width"));
        Assert.Equal("316", Value(Style("SkylineCredentialPasswordStyle"), "Width"));
        Assert.Equal("56", Value(Style("SkylineCredentialRevealStyle"), "Width"));
        var page = Document("MainPage.xaml").Root!;
        var password = Named(page, "PasswordInput");
        Assert.Equal("Hidden", Attribute(password, "PasswordRevealMode"));
        Assert.Equal("True", Attribute(password, "PreventKeyboardDisplayOnProgrammaticFocus"));
        Assert.Equal("Show password", Attribute(Named(page, "RevealButton"), "AutomationProperties.Name"));
        Assert.Equal("8", Attribute(Named(page, "PasswordRow").Descendants(Ui + "StackPanel").Single(), "Spacing"));
    }

    [Fact]
    public void ConsentAndRemovalHaveDifferentOfficialSurfaces()
    {
        // TvServerSetupScreen.kt AlertDialog vs TvOptionDialog.kt's compact vertical choices.
        Assert.Equal("#FF1C1C1E", Value(Style("SkylineHttpDialogStyle"), "Background"));
        Assert.Equal("560", Value(Style("SkylineHttpDialogStyle"), "Width"));
        Assert.Equal("380", Value(Style("SkylineConnectionRemoveDialogStyle"), "Width"));
        Assert.Equal("10,14,10,10", Value(Style("SkylineConnectionRemoveDialogStyle"), "Padding"));
        var dialog = Document("ConnectionDialog.xaml").Root!;
        Assert.Equal("Connect", Attribute(Named(dialog, "HttpConfirm"), "Content"));
        Assert.Equal("Cancel", Attribute(Named(dialog, "HttpCancel"), "Content"));
        Assert.Contains(dialog.Descendants(Ui + "TextBlock"), element => (string?)element.Attribute("Text") == "Connect without encryption?");
        Assert.Equal("Collapsed", Attribute(Named(dialog, "ModalLayer"), "Visibility"));
    }

    [Theory]
    [InlineData("MainPage.xaml")]
    [InlineData("ServerListPage.xaml")]
    [InlineData("ConnectionDialog.xaml")]
    [InlineData("SavedServerRow.xaml")]
    public void ConnectionPagesReferenceExistingThemeResources(string file)
    {
        var resources = Theme.Root!.Elements().Select(element => (string?)element.Attribute(Xaml + "Key"))
            .Where(key => key is not null).ToHashSet(StringComparer.Ordinal);
        foreach (var attribute in Document(file).Descendants().Attributes())
        {
            const string prefix = "{StaticResource ";
            if (attribute.Value.StartsWith(prefix, StringComparison.Ordinal)) Assert.Contains(attribute.Value[prefix.Length..^1], resources);
        }
    }

    [Theory]
    [InlineData("SkylineSavedServerButtonStyle")]
    [InlineData("SkylineCredentialFieldStyle")]
    [InlineData("SkylineCredentialPasswordStyle")]
    public void NewTemplatesHaveNoImplicitTransitions(string key) =>
        Assert.DoesNotContain(Style(key).Descendants(), element => element.Name.LocalName is "BrushTransition" or "ScalarTransition");

    private static XDocument Document(string file) => XDocument.Load(Path.Combine(AppContext.BaseDirectory, file));
    private static XElement Resource(string key) => Theme.Root!.Elements().Single(element => (string?)element.Attribute(Xaml + "Key") == key);
    private static XElement Style(string key) => Resource(key);
    private static XElement Named(XElement root, string name) => root.Descendants().Single(element => (string?)element.Attribute(Xaml + "Name") == name);
    private static string Attribute(XElement element, XName name) => (string)element.Attribute(name)!;
    private static string Value(XElement style, string property) => Attribute(style.Elements(Ui + "Setter").Single(setter => (string?)setter.Attribute("Property") == property), "Value");
    private static string StateValue(XElement state, string target) => Attribute(state.Descendants(Ui + "Setter").Single(setter => (string?)setter.Attribute("Target") == target), "Value");
}
