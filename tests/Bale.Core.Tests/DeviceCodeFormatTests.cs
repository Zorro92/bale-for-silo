using Bale.Core.Auth;

namespace Bale.Core.Tests;

public class DeviceCodeFormatTests
{
    [Theory]
    [InlineData("4821-7730", "4821 7730")]
    [InlineData("abcdefgh", "ABCD EFGH")]
    [InlineData("12-34", "12 34")]
    public void Displays(string code, string expected) => Assert.Equal(expected, DeviceCodeFormat.Display(code));

    [Theory]
    [InlineData("https://silo.example.com/activate/", "", "silo.example.com/activate")]
    [InlineData("http://10.0.0.2:8080/activate", "", "http://10.0.0.2:8080/activate")]
    [InlineData("", "https://silo.example.com/activate?code=1", "silo.example.com/activate")]
    public void ActivateText(string uri, string complete, string expected) =>
        Assert.Equal(expected, DeviceCodeFormat.ActivateText(uri, complete));
}
