using System.Globalization;
using Bale.Core.Text;

namespace Bale.Core.Tests;

public class CountFormatTests
{
    [Fact]
    public void GroupsThousandsInTheCulturesStyle()
    {
        var english = CultureInfo.GetCultureInfo("en-US");
        Assert.Equal("1,284 titles", CountFormat.Noun(1284, "title", "titles", english));
        Assert.Equal("1 title", CountFormat.Noun(1, "title", "titles", english));
        Assert.Equal("999 items", CountFormat.Noun(999, "item", "items", english));
        Assert.Equal("1.284", CountFormat.Grouped(1284, CultureInfo.GetCultureInfo("de-DE")));
    }
}
