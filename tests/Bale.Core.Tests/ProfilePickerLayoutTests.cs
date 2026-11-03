using System.Xml.Linq;

namespace Bale.Core.Tests;

/// <summary>TvProfileSelectionScreen.kt declarative guards; not a UWP build or render check.</summary>
public sealed class ProfilePickerLayoutTests
{
    private static readonly XNamespace Ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static XDocument Load(string name) => XDocument.Load(Path.Combine(AppContext.BaseDirectory, name));
    private static XElement Named(XDocument document, string name) => document.Descendants()
        .Single(element => (string?)element.Attribute(Xaml + "Name") == name);

    [Fact]
    public void ManagePrecedesExistingUtilitiesAndReusesThePickerPinPrompt()
    {
        var picker = Load("ProfilesPage.xaml");
        var actions = Named(picker, "PickerActions").Elements(Ui + "Button");
        Assert.Equal(["ManageButton", "ChangeServerButton", "SignOutButton"], actions.Select(button => (string?)button.Attribute(Xaml + "Name")));
        Assert.Equal("OnManageClick", (string?)Named(picker, "ManageButton").Attribute("Click"));
        Assert.Equal("Collapsed", (string?)Named(picker, "ManageButton").Attribute("Visibility"));
        Assert.Single(picker.Descendants(), element => element.Name.LocalName == "ProfilePinDialog");
        Assert.Equal("ConnectionDialog", Named(picker, "DeletePrompt").Name.LocalName);
    }

    [Fact]
    public void OnlyTheAvatarIsDimmedWithTheRingBehindBadgesAndThePencilAboveIt()
    {
        var picker = Load("ProfilesPage.xaml");
        var art = Named(picker, "Art");
        var avatar = art.Elements(Ui + "Grid").Single(element => (string?)element.Attribute("Opacity") == "{x:Bind AvatarOpacity}");
        Assert.Equal("{x:Bind ProfileVisibility}", (string?)avatar.Attribute("Visibility"));
        var pencil = art.Elements(Ui + "Viewbox").Single();
        Assert.Equal("{x:Bind PencilVisibility}", (string?)pencil.Attribute("Visibility"));
        // TvProfileSelectionScreen.kt's focusRing is drawBehind, below the avatar's KIDS/lock badges.
        Assert.Equal("FocusRing", (string?)art.Elements().First().Attribute(Xaml + "Name"));
        Assert.Same(pencil, art.Elements().Last());
        var delete = Named(picker, "DeleteButton");
        Assert.Equal("{x:Bind DeleteVisibility}", (string?)delete.Attribute("Visibility"));
        Assert.Equal("OnDeleteProfileClick", (string?)delete.Attribute("Click"));
    }

    [Fact]
    public void AddUsesTheOfficialDashedCircleAndManagedCellsReserveDeleteHeight()
    {
        var theme = Load("Skyline.xaml");
        XElement Style(string key) => theme.Root!.Elements(Ui + "Style")
            .Single(style => (string?)style.Attribute(Xaml + "Key") == key);
        string? Value(string key, string property) => (string?)Style(key).Elements(Ui + "Setter")
            .Single(setter => (string?)setter.Attribute("Property") == property).Attribute("Value");
        Assert.Equal("2", Value("SkylineProfileAddOutlineStyle", "StrokeThickness"));
        Assert.Equal("3,2.5", (string?)Named(Load("ProfilesPage.xaml"), "AddOutline").Attribute("StrokeDashArray"));
        Assert.DoesNotContain(Style("SkylineProfileAddOutlineStyle").Elements(Ui + "Setter"),
            setter => (string?)setter.Attribute("Property") == "StrokeDashArray");
        Assert.Equal("30", Value("SkylineProfilePencilStyle", "Width"));
        Assert.Equal("33", Value("SkylineProfileAddIconStyle", "Width"));
        Assert.Equal("184", Value("SkylineProfileManageExtentStyle", "Height"));
    }

    [Theory]
    [InlineData("SkylineProfileAddIconStyle")]
    [InlineData("SkylineProfilePencilStyle")]
    public void AddAndPencilPathsPreserveTheMaterialIconViewport(string key)
    {
        var icon = Load("ProfilesPage.xaml").Descendants(Ui + "Viewbox")
            .Single(element => (string?)element.Attribute("Style") == $"{{StaticResource {key}}}")
            .Elements(Ui + "Path").Single();
        Assert.Equal("24", (string?)icon.Attribute("Width"));
        Assert.Equal("24", (string?)icon.Attribute("Height"));
    }
}
