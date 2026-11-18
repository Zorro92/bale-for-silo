using Bale.Core.Connection;

namespace Bale.Core.Tests;

public class ServerSetupAddressTests
{
    [Fact]
    public void BareHostTriesHttpsThenHttpThenSiloPortAndPreservesPathCase()
    {
        Assert.Equal(new[] { "https://example.com/Prefix", "http://example.com/Prefix", "http://example.com:8090/Prefix" },
            ServerSetupAddress.Candidates("  EXAMPLE.com/Prefix/  ").Select(ServerAddress.ToBaseUrl));
    }

    [Theory]
    [InlineData("http://example.com", ServerSetupScheme.Auto, "", "http://example.com")]
    [InlineData("https://example.com:8443/path", ServerSetupScheme.Http, "8090", "http://example.com:8443/path")]
    [InlineData("example.com/path", ServerSetupScheme.Https, "8090", "https://example.com:8090/path")]
    [InlineData("http://192.0.2.10:8090/", ServerSetupScheme.Auto, "", "http://192.0.2.10:8090")]
    public void HonorsSchemeAndAddressPortOverrides(string input, ServerSetupScheme scheme, string port, string expected)
    {
        Assert.Equal(expected, ServerAddress.ToBaseUrl(Assert.Single(ServerSetupAddress.Candidates(input, scheme, port))));
    }

    [Theory]
    // Apple d1837b92: only the Port field's own value is the Port field's fault; a port typed into the address is
    // the address's.
    [InlineData("", "", "Please enter a server host.", false)]
    [InlineData("https://", "", "Enter a valid server address.", false)]
    [InlineData("ftp://example.com", "", "Enter a valid server address.", false)]
    [InlineData("example.com", "0", "Port must be a number between 1 and 65535.", true)]
    [InlineData("example.com", "65536", "Port must be a number between 1 and 65535.", true)]
    [InlineData("example.com", "abc", "Port must be a number between 1 and 65535.", true)]
    [InlineData("https://example.com:0", "", "Port must be a number between 1 and 65535.", false)]
    [InlineData("https://example.com:0", "8443", "Port must be a number between 1 and 65535.", false)]
    public void UsesOfficialValidationMessages(string input, string port, string message, bool portField)
    {
        var error = Assert.Throws<ServerAddressException>(() => ServerSetupAddress.Candidates(input, port: port));
        Assert.Equal(message, error.Message);
        Assert.Equal(portField, error.PortField);
    }

    [Fact]
    public void AdvancedPortAppliesToBothAutoCandidatesWithoutDefaultSiloPort()
    {
        Assert.Equal(new[] { "https://example.com:8443", "http://example.com:8443" },
            ServerSetupAddress.Candidates("example.com", port: "8443").Select(ServerAddress.ToBaseUrl));
    }

    [Theory]
    [InlineData("example.com", ServerSetupScheme.Auto, false)]
    [InlineData("HTTP://example.com", ServerSetupScheme.Auto, true)]
    [InlineData("http://example.com", ServerSetupScheme.Https, false)]
    [InlineData("example.com", ServerSetupScheme.Http, true)]
    [InlineData("", ServerSetupScheme.Http, false)]
    public void CleartextHintDoesNotGuessAnAutoFallback(string input, ServerSetupScheme scheme, bool expected)
    {
        Assert.Equal(expected, ServerSetupAddress.UsesCleartext(input, scheme));
    }
}
