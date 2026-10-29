using Bale.Core.Connection;

namespace Bale.Core.Tests;

public class ServerAddressTests
{
    [Theory]
    [InlineData("silo.example.com", "https://silo.example.com")]
    [InlineData("  https://silo.example.com/  ", "https://silo.example.com")]
    [InlineData("http://192.168.1.10:8080", "http://192.168.1.10:8080")]
    [InlineData("https://example.com/silo/", "https://example.com/silo")]
    public void Normalizes(string input, string expected)
    {
        Assert.True(ServerAddress.TryParse(input, out var address));
        Assert.Equal(expected, ServerAddress.ToBaseUrl(address));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ftp://silo.example.com")]
    [InlineData("https://")]
    public void Rejects(string? input)
    {
        Assert.False(ServerAddress.TryParse(input, out _));
    }
}
