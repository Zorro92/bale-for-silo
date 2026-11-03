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

    [Fact]
    public void EditorMatchesOfficialGeometryWithoutInventingPreferenceSections()
    {
        var theme = Load("Skyline.xaml");
        string? Value(string key, string property) => (string?)theme.Root!.Elements(Ui + "Style")
            .Single(style => (string?)style.Attribute(Xaml + "Key") == key).Elements(Ui + "Setter")
            .Single(setter => (string?)setter.Attribute("Property") == property).Attribute("Value");
        Assert.Equal("80,60,80,0", Value("SkylineProfileEditorLayoutStyle", "Margin"));
        Assert.Equal("180", Value("SkylineProfilePreviewColumnStyle", "Width"));
        Assert.Equal("584", Value("SkylineProfileFormScrollerStyle", "Width"));
        Assert.Equal("154", Value("SkylineProfileAvatarListStyle", "Height"));
        Assert.Equal("52", Value("SkylineProfileEntryStyle", "Height"));
        Assert.Equal("48", Value("SkylineProfileSegmentStyle", "Height"));
        Assert.Equal("0,0,0,48", Value("SkylineProfileFormStackStyle", "Padding"));
        var form = Load("ProfileEditorPage.xaml");
        var labels = form.Descendants(Ui + "TextBlock").Select(element => (string?)element.Attribute("Text")).ToArray();
        Assert.Contains("STYLE", labels);
        Assert.Contains("PIN (OPTIONAL)", labels);
        Assert.Contains("Maximum rating", labels);
        Assert.DoesNotContain(labels, label => label?.Contains("Quality", StringComparison.OrdinalIgnoreCase) == true
            || label?.Contains("Subtitle", StringComparison.OrdinalIgnoreCase) == true);
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
