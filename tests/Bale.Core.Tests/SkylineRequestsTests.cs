using System.Xml.Linq;

namespace Bale.Core.Tests;

/// <summary>Declarative Requests parity guards; these do not compile or render UWP.</summary>
public class SkylineRequestsTests
{
    private static readonly XNamespace Ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XDocument Theme = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Skyline.xaml"));

    [Fact]
    public void DensePostersUseOfficialDimensionsAndSharedRailLayout()
    {
        // TvRequestComponents.kt: the default dense poster is 88 × 132dp, caption gap 11dp.
        Assert.Equal("88", Resource("SkylineRequestPosterWidth").Value);
        Assert.Equal("132", Resource("SkylineRequestPosterHeight").Value);
        Assert.Equal("11", Value("SkylineRequestPosterStyle", "Spacing"));
        Assert.Equal("{StaticResource SkylineBrowseRailStyle}", (string?)Resource("SkylineRequestRailStyle").Attribute("BasedOn"));
        Assert.Equal("{StaticResource SkylineRequestPosterWidth}", Value("SkylineRequestRailStyle", "services:RailLayout.CardWidth"));
        Assert.Equal("{StaticResource SkylineRequestPosterHeight}", Value("SkylineRequestRailStyle", "services:RailLayout.CardHeight"));
        Assert.Equal("{StaticResource SkylineRequestRailStyle}", (string?)Resource("SkylineRequestDetailRailStyle").Attribute("BasedOn"));
        Assert.Equal("50,8,50,8", Value("SkylineRequestDetailRailStyle", "services:RailLayout.ContentInset"));
    }

    [Fact]
    public void PosterAndActionTypographyApplyTheTvFontScale()
    {
        // TvRequestComponents.kt / TvRequestDetailScreen.kt: sp × TvUiFontScale (.86).
        Assert.Equal("13.33", Value("SkylineRequestCardTitleStyle", "FontSize"));
        Assert.Equal("15.91", Value("SkylineRequestCardTitleStyle", "LineHeight"));
        Assert.Equal("12.04", Value("SkylineRequestSecondaryStyle", "FontSize"));
        Assert.Equal("15.48", Value("SkylineRequestSecondaryStyle", "LineHeight"));
        Assert.Equal("#C7FFFFFF", Value("SkylineRequestCardTitleStyle", "Foreground"));
        Assert.Equal("White", Value("SkylineRequestFocusedTitleStyle", "Foreground"));
        Assert.Equal("12.04", Value("SkylineRequestPrimaryStyle", "FontSize"));
        Assert.Equal("12.04", Value("SkylineRequestActionStyle", "FontSize"));
    }

    [Fact]
    public void StatusColorsAndFourStageTrackMatchTheOfficialComponents()
    {
        Assert.Equal("#FFF59E0B", (string?)Resource("SkylineRequestAmber").Attribute("Color"));
        Assert.Equal("#FF38BDF8", (string?)Resource("SkylineRequestSky").Attribute("Color"));
        Assert.Equal("#FF34D399", (string?)Resource("SkylineRequestEmerald").Attribute("Color"));
        Assert.Equal("#FFFB7185", (string?)Resource("SkylineRequestRose").Attribute("Color"));
        Assert.Equal("#24FFFFFF", (string?)Resource("SkylineRequestTrackRest").Attribute("Color"));
        Assert.Equal("#D1EDEDED", (string?)Resource("SkylineRequestTrackComplete").Attribute("Color"));
        Assert.Equal("420", Resource("SkylineRequestDetailTrackWidth").Value);
        Assert.Equal("120", Resource("SkylineRequestPreviewTrackWidth").Value);
        Assert.Equal("150", Resource("SkylineRequestFieldWidth").Value);
        Assert.Equal("3", Value("SkylineRequestTrackStyle", "ColumnSpacing"));
        var fill = Resource("SkylineRequestSegmentStyle").Descendants(Ui + "Border")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "Fill");
        Assert.Equal("3", (string?)fill.Attribute("Height"));
        Assert.Equal("1.5", (string?)fill.Attribute("CornerRadius"));
    }

    [Fact]
    public void ActionNoticesShareThePillRowInsetAndPillsKeepOfficialGeometry()
    {
        // TvRequestDetailScreen.kt: notices align with the 38dp actions, not the hero above them.
        Assert.Equal("38", Value("SkylinePlayButtonStyle", "Height"));
        foreach (var key in new[] { "SkylineRequestPrimaryStyle", "SkylineRequestActionStyle" })
            Assert.Equal("{StaticResource SkylinePlayButtonStyle}", (string?)Resource(key).Attribute("BasedOn"));
        Assert.Equal("20,0", Value("SkylineRequestPrimaryStyle", "Padding"));
        Assert.Equal("18,0", Value("SkylineRequestActionStyle", "Padding"));
        Assert.Equal("9", Resource("SkylineRequestActionGap").Value);
        Assert.Equal("0,13,0,0", Value("SkylineRequestActionLineStyle", "Margin"));
        Assert.DoesNotContain(Resource("SkylineRequestActionsStyle").Elements(Ui + "Setter"),
            setter => (string?)setter.Attribute("Property") == "Margin");
        Assert.Equal("Center", Value("SkylineRequestActionNoticeStyle", "VerticalAlignment"));
    }

    private static XElement Resource(string key) => Theme.Root!.Elements()
        .Single(element => (string?)element.Attribute(Xaml + "Key") == key);

    private static string Value(string key, string property) => (string)Resource(key).Elements(Ui + "Setter")
        .Single(setter => (string?)setter.Attribute("Property") == property).Attribute("Value")!;
}
