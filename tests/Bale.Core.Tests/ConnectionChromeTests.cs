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
    public void NearbyServersUseSingleLineMarqueeGlassButtonsInsteadOfSavedServerCards()
    {
        // TvServerSetupScreen.kt FoundServerRows / TvMarquee.kt: 38dp Glass, 9dp gaps, inline labels.
        var page = Document("MainPage.xaml").Root!;
        var panel = Style("SkylineConnectionNearbyPanelStyle");
        Assert.Equal("450", Value(panel, "Width"));
        Assert.Equal("0,24,0,0", Value(panel, "Margin"));
        Assert.Equal("9", Value(panel, "Spacing"));
        Assert.Equal("{StaticResource SkylineAuthTertiary}", Value(Style("SkylineConnectionNearbyCaptionStyle"), "Foreground"));
        Assert.Equal("12.04", Value(Style("SkylineAuthBodyStyle"), "FontSize"));
        Assert.Equal("{StaticResource SkylineAuthButtonStyle}", Attribute(Style("SkylineConnectionNearbyButtonStyle"), "BasedOn"));
        Assert.Equal("38", Value(Style("SkylineAuthButtonStyle"), "Height"));
        Assert.Equal("19", Value(Style("SkylineAuthButtonStyle"), "CornerRadius"));
        Assert.Equal("20,0", Value(Style("SkylineAuthButtonStyle"), "Padding"));
        Assert.Equal("12.47", Value(Style("SkylineAuthButtonStyle"), "FontSize"));
        Assert.Equal("7", Value(Style("SkylineConnectionNearbyContentStyle"), "ColumnSpacing"));
        foreach (var name in new[] { "NearbyFirst", "NearbySecond" })
        {
            var button = Named(page, name);
            Assert.Equal("{StaticResource SkylineConnectionNearbyButtonStyle}", Attribute(button, "Style"));
            Assert.Single(button.Elements(Ui + "Grid"));
            Assert.Equal("{StaticResource SkylineConnectionNearbyNameStyle}", Attribute(Named(button, name + "Name"), "Style"));
            Assert.Equal("{StaticResource SkylineConnectionNearbyDetailStyle}", Attribute(Named(button, name + "Address"), "Style"));
            Assert.Equal("1", Attribute(Named(button, name + "Address"), "Grid.Column"));
        }
        var label = Style("SkylineConnectionNearbyNameStyle");
        var detail = Style("SkylineConnectionNearbyDetailStyle");
        Assert.Equal("1", Value(label, "MaxLines"));
        Assert.Equal("NoWrap", Value(label, "TextWrapping"));
        Assert.Equal("CharacterEllipsis", Value(label, "TextTrimming"));
        Assert.Equal("{StaticResource SkylineConnectionNearbyNameStyle}", Attribute(detail, "BasedOn"));
        Assert.Equal("12.04", Value(detail, "FontSize"));
        Assert.Equal("Normal", Value(detail, "FontWeight"));
        Assert.Equal("0.62", Value(detail, "Opacity"));
        // Both labels inherit the marquee button's ink, including black when focused.
        Assert.DoesNotContain(label.Elements(Ui + "Setter").Concat(detail.Elements(Ui + "Setter")),
            setter => (string?)setter.Attribute("Property") == "Foreground");
    }

    [Fact]
    public void ServerEntryKeepsTheOfficialPhoneCardBesideItsVerticallyScrollableCopy()
    {
        // TvServerSetupScreen.kt AddressEntry / TvMarquee.kt TvUsePhoneCard.
        var page = Document("MainPage.xaml").Root!;
        var panel = Named(page, "ServerPanel");
        Assert.Equal(Ui + "Grid", panel.Name);
        Assert.Equal(new[] { "{StaticResource SkylineAuthCopyColumn}", "*", "{StaticResource SkylineAuthCardColumn}" },
            panel.Element(Ui + "Grid.ColumnDefinitions")!.Elements().Select(column => Attribute(column, "Width")).ToArray());
        var scroller = Named(panel, "ServerScroller");
        Assert.Equal("OnServerControlFocus", Attribute(scroller, "GotFocus"));
        Assert.Equal("OnServerViewportChanged", Attribute(scroller, "SizeChanged"));
        Assert.Equal("OnServerBringIntoViewRequested", Attribute(scroller, "BringIntoViewRequested"));
        Assert.Equal("OnServerViewportChanged", Attribute(Named(panel, "ServerCopy"), "SizeChanged"));
        Assert.Equal("Disabled", Value(Style("SkylineServerEntryScrollStyle"), "HorizontalScrollMode"));
        Assert.Equal("Auto", Value(Style("SkylineServerEntryScrollStyle"), "VerticalScrollMode"));
        var card = Named(panel, "ServerPhoneCard");
        Assert.Equal("2", Attribute(card, "Grid.Column"));
        Assert.Equal("{StaticResource SkylineServerPhoneCardStyle}", Attribute(card, "Style"));
        Assert.Equal("{StaticResource SkylineLoginCardStyle}", Attribute(Style("SkylineServerPhoneCardStyle"), "BasedOn"));
        Assert.Equal("Center", Value(Style("SkylineServerPhoneCardStyle"), "VerticalAlignment"));
        Assert.Contains(card.Descendants(Ui + "TextBlock"), text => (string?)text.Attribute("Text") == "Easier with a phone");
        Assert.Contains(card.Descendants(Ui + "TextBlock"), text => (string?)text.Attribute("Text") ==
            "Open Silo on a phone or tablet on the same Wi‑Fi and tap Set up. It finishes this for you.");
        Assert.Equal("56", Value(Style("SkylineServerPhoneSymbolStyle"), "Width"));
        Assert.Equal("56", Value(Style("SkylineServerPhoneSymbolStyle"), "Height"));
        Assert.Equal("25.76", Value(Style("SkylineServerPhoneIconStyle"), "Width"));
        Assert.Equal("17.2", Value(Style("SkylineServerPhoneTitleStyle"), "FontSize"));
        Assert.Equal("0,17,0,0", Value(Style("SkylineServerPhoneTitleStyle"), "Margin"));
        Assert.Equal("Center", Value(Style("SkylineServerPhoneBodyStyle"), "TextAlignment"));
        Assert.Equal("0,7,0,0", Value(Style("SkylineServerPhoneBodyStyle"), "Margin"));
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
