using System.Xml.Linq;

namespace Bale.Core.Tests;

/// <summary>TvDetailTrailersSection.kt focus lift; declarative guard, not a UWP render check.</summary>
public sealed class DetailTrailerLayoutTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void TheTrailerLiftIsInsideTheTemplateRootSoThePressTiltCanProjectTheRoot()
    {
        // Gamepad A sets a Projection on the list item's content root; with a ScaleTransition
        // on that same element UWP throws UnauthorizedAccessException and the app ends.
        var detail = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "DetailPage.xaml"));
        var template = detail.Descendants().Single(element => (string?)element.Attribute(Xaml + "Key") == "TrailerTemplate");
        var root = template.Elements().Single();
        Assert.Null(root.Attribute(Xaml + "Name"));
        var lift = root.Elements().Single();
        Assert.Equal("TrailerLift", (string?)lift.Attribute(Xaml + "Name"));
        Assert.Contains(lift.Descendants(), element => (string?)element.Attribute(Xaml + "Name") == "TrailerFocusRing");
    }
}
