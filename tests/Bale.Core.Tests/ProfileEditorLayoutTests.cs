using System.Xml.Linq;
using Bale.Core.Api.Models;
using Bale.Core.Profiles;

namespace Bale.Core.Tests;

/// <summary>Declarative parity checks only: Linux cannot render or compile UWP.</summary>
public sealed class ProfileEditorLayoutTests
{
    private static readonly XNamespace Ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static XDocument Load(string name) => XDocument.Load(Path.Combine(AppContext.BaseDirectory, name));

    [Fact]
    public void NameAndPinOnlyBecomeTextInputsInsideTheExplicitDialog()
    {
        var form = Load("ProfileEditorPage.xaml");
        Assert.Empty(form.Descendants(Ui + "TextBox"));
        Assert.Empty(form.Descendants(Ui + "PasswordBox"));
        foreach (var name in new[] { "NameField", "PinField" })
            Assert.Equal(Ui + "Button", form.Descendants().Single(element => (string?)element.Attribute(Xaml + "Name") == name).Name);
        var modal = Load("ProfileTextDialog.xaml");
        foreach (var input in modal.Descendants().Where(element => element.Name.LocalName is "TextBox" or "PasswordBox"))
            Assert.Equal("True", (string?)input.Attribute("PreventKeyboardDisplayOnProgrammaticFocus"));
        Assert.Equal("NumericPin", (string?)modal.Descendants(Ui + "PasswordBox").Single().Attribute("InputScope"));
    }

    [Theory]
    [InlineData("TextBox", "SkylineProfileTextFieldStyle")]
    [InlineData("PasswordBox", "SkylineProfileTextPasswordStyle")]
    public void TextDialogFieldsStretchWithinTheCardAndRetainNativeEditingParts(string type, string key)
    {
        var modal = Load("ProfileTextDialog.xaml");
        var input = modal.Descendants(Ui + type).Single();
        Assert.Equal($"{{StaticResource {key}}}", (string?)input.Attribute("Style"));
        var style = Load("Skyline.xaml").Root!.Elements(Ui + "Style")
            .Single(element => (string?)element.Attribute(Xaml + "Key") == key);
        string? Value(string property) => (string?)style.Elements(Ui + "Setter")
            .Single(setter => (string?)setter.Attribute("Property") == property).Attribute("Value");
        Assert.Equal("Auto", Value("Width"));
        Assert.Equal("Stretch", Value("HorizontalAlignment"));
        Assert.Equal("56", Value("Height"));
        Assert.Contains(style.Descendants(Ui + "ScrollViewer"), element => (string?)element.Attribute(Xaml + "Name") == "ContentElement");
        Assert.Contains(style.Descendants(Ui + "ContentControl"), element => (string?)element.Attribute("Content") == "{TemplateBinding Header}");
        Assert.Empty(style.Descendants(Ui + "Path")); // No inherited Login credential icon.
    }

    [Fact]
    public void RemoteFocusIncludesTrailingActionsWithoutChangingTheGridColumnOrPinVerticalLinks()
    {
        // Declarative/source guards only, not execution of UWP's focus system.
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ProfileEditorPage.cs.txt"));
        Assert.Contains("target / 6 == index / 6", source);
        Assert.Contains("var target = index + x + y * 6;", source);
        Assert.Contains("else FocusTarget(ShuffleButton);", source);
        Assert.Contains("if (focused == PinField && x > 0 && RemovePinButton.Visibility == Visibility.Visible) FocusTarget(RemovePinButton);", source);
        Assert.Contains("else if (focused == RemovePinButton && x < 0) FocusTarget(PinField);", source);
        Assert.Contains("if (y > 0) FocusTarget(PinField);", source);
        Assert.Contains("else if (focused == PinField) FocusTarget(y > 0 ? ChildSwitch : NameField);", source);
        Assert.Contains("else if (focused == RemovePinButton) FocusTarget(y > 0 ? PinField : NameField);", source);
    }

    [Fact]
    public void EditorMatchesOfficialGeometryWithoutInventingPreferenceSections()
    {
        var theme = Load("Skyline.xaml");
        string? Value(string key, string property) => (string?)theme.Root!.Elements(Ui + "Style")
            .Single(style => (string?)style.Attribute(Xaml + "Key") == key).Elements(Ui + "Setter")
            .Single(setter => (string?)setter.Attribute("Property") == property).Attribute("Value");
        Assert.Equal("80,60,80,0", Value("SkylineProfileEditorLayoutStyle", "Margin"));
        Assert.Equal("180", Value("SkylineProfilePreviewColumnStyle", "Width"));
        // TvProfileForm.kt's 584dp column gets the 580dp left in its row; the scroller overhangs 16dp each side.
        Assert.Equal("612", Value("SkylineProfileFormScrollerStyle", "Width"));
        Assert.Equal("-16,0,-16,0", Value("SkylineProfileFormScrollerStyle", "Margin"));
        Assert.Equal("586", Value("SkylineProfileAvatarListStyle", "Width"));
        Assert.Equal("154", Value("SkylineProfileAvatarListStyle", "Height"));
        Assert.Equal("52", Value("SkylineProfileEntryStyle", "Height"));
        Assert.Equal("48", Value("SkylineProfileSegmentStyle", "Height"));
        Assert.Equal("16,0,16,48", Value("SkylineProfileFormStackStyle", "Padding"));
        var form = Load("ProfileEditorPage.xaml");
        var labels = form.Descendants(Ui + "TextBlock").Select(element => (string?)element.Attribute("Text")).ToArray();
        Assert.Contains("STYLE", labels);
        Assert.Contains("PIN (OPTIONAL)", labels);
        Assert.Contains("Maximum rating", labels);
        Assert.DoesNotContain(labels, label => label?.Contains("Quality", StringComparison.OrdinalIgnoreCase) == true
            || label?.Contains("Subtitle", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void FormUsesHeroPillsRatherThanMarqueeLoginButtons()
    {
        var theme = Load("Skyline.xaml");
        XElement Style(string key) => theme.Root!.Elements(Ui + "Style").Single(style => (string?)style.Attribute(Xaml + "Key") == key);
        string? Value(string key, string property) => (string?)Style(key).Elements(Ui + "Setter")
            .Single(setter => (string?)setter.Attribute("Property") == property).Attribute("Value");
        Assert.Equal("58", Value("SkylineProfileSubmitStyle", "Height"));
        Assert.Equal("29", Value("SkylineProfileSubmitStyle", "CornerRadius"));
        Assert.Equal("32,0", Value("SkylineProfileSubmitStyle", "Padding"));
        Assert.Equal("15.48", Value("SkylineProfileSubmitStyle", "FontSize"));
        Assert.Equal("24", Value("SkylineProfileSmallActionStyle", "Height"));
        Assert.Equal("13.76", Value("SkylineProfileSmallActionStyle", "FontSize"));
        Assert.Equal("#6B000000", Value("SkylineProfileSmallActionStyle", "Background"));
        Assert.Equal("22", Value("SkylineProfileCancelStyle", "Height"));
        Assert.Equal("32", Value("SkylineProfileRetryLibrariesStyle", "Height"));
        Assert.Equal("14,0", Value("SkylineProfileRetryLibrariesStyle", "Padding"));
        Assert.Equal("10", Value("SkylineProfileActionContentStyle", "Spacing"));
        Assert.Single(Style("SkylineProfileSubmitStyle").Descendants(Ui + "ContentPresenter"));
        Assert.Equal("17.2", Value("SkylineProfilePreviewNameStyle", "FontSize"));
        Assert.Equal("SemiBold", Value("SkylineProfilePreviewNameStyle", "FontWeight"));
    }

    [Theory]
    [InlineData("SkylineProfileSubmitStyle", "20", "0.20", "1.05")]
    [InlineData("SkylineProfileAvatarChoiceStyle", "16", "0.18", "1.08")]
    [InlineData("SkylineProfileSegmentStyle", "14", "0.45", "1.06")]
    public void FormFocusUsesTheOfficialWhiteCenteredGlowAndScale(string key, string elevation, string opacity, string scale)
    {
        var style = Load("Skyline.xaml").Root!.Elements(Ui + "Style").Single(element => (string?)element.Attribute(Xaml + "Key") == key);
        string? Value(string property) => (string?)style.Elements(Ui + "Setter")
            .Single(setter => (string?)setter.Attribute("Property") == property).Attribute("Value");
        Assert.Equal(elevation, Value("services:SurfaceShadow.Elevation"));
        Assert.Equal("White", Value("services:SurfaceShadow.Color"));
        Assert.Equal(opacity, Value("services:SurfaceShadow.Opacity"));
        Assert.Equal("0", Value("services:SurfaceShadow.OffsetScale"));
        var focused = style.Descendants(Ui + "VisualState").Single(state => (string?)state.Attribute(Xaml + "Name") == "Focused");
        Assert.Contains(focused.Descendants(Ui + "Setter"), setter => (string?)setter.Attribute("Target") == "Lift.ScaleX" && (string?)setter.Attribute("Value") == scale);
        Assert.Contains(focused.Descendants(Ui + "Setter"), setter => (string?)setter.Attribute("Target") == "ShadowHost.Opacity" && (string?)setter.Attribute("Value") == "1");
        Assert.DoesNotContain(style.Descendants(), element => element.Name.LocalName.EndsWith("Transition", StringComparison.Ordinal));
    }

    [Fact]
    public void AccessRowsUseTheOfficialFocusWashWithoutEntryFieldScaling()
    {
        var theme = Load("Skyline.xaml");
        XElement Style(string key) => theme.Root!.Elements(Ui + "Style").Single(style => (string?)style.Attribute(Xaml + "Key") == key);
        var rows = Style("SkylineProfileSwitchStyle");
        Assert.Contains(rows.Elements(Ui + "Setter"), setter => (string?)setter.Attribute("Property") == "Background" && (string?)setter.Attribute("Value") == "#14FFFFFF");
        Assert.Contains(Style("SkylineProfileLibraryCheckStyle").Elements(Ui + "Setter"), setter => (string?)setter.Attribute("Property") == "Background" && (string?)setter.Attribute("Value") == "#1AFFFFFF");
        Assert.Contains(rows.Descendants(Ui + "Setter"), setter => (string?)setter.Attribute("Target") == "Lift.ScaleX" && (string?)setter.Attribute("Value") == "1.02");
        Assert.Contains(rows.Descendants(Ui + "Border"), border => (string?)border.Attribute("BorderBrush") == "#59FFFFFF" && (string?)border.Attribute("BorderThickness") == "1");
        Assert.Single(rows.Descendants(Ui + "ContentPresenter"));
        // Only connected text segments can use the selected/unselected double-presenter template.
        var form = Load("ProfileEditorPage.xaml");
        foreach (var segment in form.Descendants(Ui + "ToggleButton").Where(button => (string?)button.Attribute("Style") == "{StaticResource SkylineProfileSegmentStyle}"))
        {
            Assert.NotNull(segment.Attribute("Content"));
            Assert.Empty(segment.Elements());
        }
    }

    [Theory]
    [InlineData("CancelButton", "CancelLabel")]
    [InlineData("ShuffleButton", "ShuffleLabel")]
    [InlineData("RemovePinButton", "RemovePinLabel")]
    [InlineData("RetryLibraries", "RetryLibrariesLabel")]
    [InlineData("SubmitButton", "SubmitLabel")]
    public void InlineActionIconsFollowTheirLabelInkWithoutReplacingElementContent(string button, string label)
    {
        var action = Load("ProfileEditorPage.xaml").Descendants(Ui + "Button")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == button);
        var icon = action.Descendants(Ui + "Viewbox").Single().Element(Ui + "Path")!;
        Assert.Equal("24", (string?)icon.Attribute("Width"));
        Assert.Equal("24", (string?)icon.Attribute("Height"));
        Assert.Equal($"{{Binding Foreground, ElementName={label}}}", (string?)icon.Attribute("Fill"));
        Assert.Contains(action.Descendants(Ui + "TextBlock"), element => (string?)element.Attribute(Xaml + "Name") == label);
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ProfileEditorPage.cs.txt"));
        Assert.DoesNotContain($"{button}.Content =", source);
    }

    [Fact]
    public void SharedEditorUsesTheProfileAuroraNotThePickerMarqueeBackdrop()
    {
        var form = Load("ProfileEditorPage.xaml");
        Assert.Equal("{StaticResource SkylineProfileEditorBackdrop}", (string?)form.Root!.Attribute("Background"));
        var backdrop = Load("Skyline.xaml").Root!.Elements(Ui + "ImageBrush")
            .Single(brush => (string?)brush.Attribute(Xaml + "Key") == "SkylineProfileEditorBackdrop");
        Assert.Equal("ms-appx:///Assets/ProfileEditorBackdrop.png", (string?)backdrop.Attribute("ImageSource"));
    }

    [Fact]
    public void AssignableLibrariesHideReadingMediaAndPreserveServerOrder()
    {
        UserLibrary[] libraries = [
            new() { Id = "music", Type = "music", SortOrder = 4 },
            new() { Id = "books", Type = "books", SortOrder = 0 },
            new() { Id = "shows", Type = "shows", SortOrder = 2 },
            new() { Id = "audio", Type = "audiobook", SortOrder = 3 },
            new() { Id = "comics", Type = "comic", SortOrder = 1 },
            new() { Id = "movies", Type = "movies", SortOrder = 1 },
            new() { Type = "movies", SortOrder = 0 },
        ];
        Assert.Equal(["movies", "shows", "audio", "music"], ProfileLibraries.ShownOnTv(libraries).Select(library => library.Id));
    }
}
