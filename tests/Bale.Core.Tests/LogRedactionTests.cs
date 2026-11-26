using Bale.Core.Api.Models;
using Bale.Core.Text;
using Microsoft.Kiota.Abstractions;

namespace Bale.Core.Tests;

/// <summary>
/// What bale.log may hold (Core LogRedaction, applied by the Xbox app's Services/Log.cs to every line): credentials never;
/// in production builds also no URLs, query strings, addresses or the server's problem detail.
/// </summary>
public sealed class LogRedactionTests
{
    [Theory]
    [InlineData("stream https://media.example.com/api/v2/stream/s/f?st=abc123&start=5",
        "stream https://media.example.com/api/v2/stream/s/f?st=[redacted]&start=5")]
    [InlineData("retry ?access_token=eyJ.x.y;refresh_token=r1 id_token=i2", "retry ?access_token=[redacted];refresh_token=[redacted] id_token=[redacted]")]
    [InlineData("Authorization: Bearer eyJhbGciOi.payload.signature", "Authorization: [redacted]")]
    [InlineData("headers authorization=abc, accept=*/*", "headers authorization=[redacted], accept=*/*")]
    [InlineData("rejected Bearer abc.DEF-ghi_9~+/= after refresh", "rejected Bearer [redacted] after refresh")]
    [InlineData("{\"access_token\":\"abc\",\"refresh_token\": \"def\",\"expires_in\":3600}",
        "{\"access_token\":\"[redacted]\",\"refresh_token\":\"[redacted]\",\"expires_in\":3600}")]
    [InlineData("sig=1 signature=2 api_key=3 apikey=4 password=5 secret=6",
        "sig=[redacted] signature=[redacted] api_key=[redacted] apikey=[redacted] password=[redacted] secret=[redacted]")]
    public void CredentialsAreMaskedInEveryBuild(string line, string masked)
    {
        Assert.Equal(masked, LogRedaction.MaskCredentials(line));
        // Production redacts at least as much.
        Assert.DoesNotContain("abc", LogRedaction.ForProduction(line).Replace("[redacted]", string.Empty, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("back: key=GamepadB original=GamepadB")]
    [InlineData("player: transport key=179 play-pause")]
    [InlineData("frame: t=1234.5 pts=20000000 dur=417083 pos=1.958 lead=12 kind=video flags=0")]
    [InlineData("first=3 last=7 start=12 tokens are fine")]
    [InlineData("Launched 812 on Windows.Xbox, package 0.1.812.0, OS 10.0.26100.4652")]
    [InlineData("player: seek target=12.5 player=10.0 ranges=1 action=native")]
    [InlineData("HTTP 404 Not Found at 12:30:45")]
    [InlineData("at Bale.Xbox.PlayerPage.OnSeek() in PlayerPage.Seek.cs:line 42")]
    public void OrdinaryDiagnosticsPassUnchanged(string line)
    {
        Assert.Equal(line, LogRedaction.MaskCredentials(line));
        Assert.Equal(line, LogRedaction.ForProduction(line));
    }

    [Theory]
    [InlineData("Artwork 404 (https://media.example.com:8096/api/v2/images/item/abc/poster.jpg)", "Artwork 404 (https://[redacted])")]
    [InlineData("open ms-appx:///Assets/x.png and http://192.0.2.20/x", "open ms-appx://[redacted] and http://[redacted]")]
    [InlineData("range /api/v2/stream/s/f?st=abc&start=5 failed", "range /api/v2/stream/s/f?[redacted] failed")]
    [InlineData("No connection could be made because the target machine actively refused it. (192.0.2.20:8096)",
        "No connection could be made because the target machine actively refused it. ([redacted]:8096)")]
    [InlineData("No such host is known. (silo.example.com:443)", "No such host is known. ([redacted]:443)")]
    [InlineData("discovered 198.51.100.7 and fe80 link", "discovered [redacted] and fe80 link")]
    [InlineData("Was it /api/x? No.", "Was it /api/x? No.")]
    public void ProductionDropsUrlsQueriesAndAddresses(string line, string redacted) =>
        Assert.Equal(redacted, LogRedaction.ForProduction(line));

    [Fact]
    public void AnApiProblemKeepsItsStatusAndTitleAndItsDetailOnlyInDebugBuilds()
    {
        var problem = new Problem { ResponseStatusCode = 403, Title = "Forbidden", Detail = "Profile 7 of alex is locked" };
        Assert.Equal("HTTP 403 Forbidden ", LogRedaction.Describe(problem, includeDetail: false));
        Assert.Equal("HTTP 403 Forbidden: Profile 7 of alex is locked ", LogRedaction.Describe(problem, includeDetail: true));
        Assert.Equal("HTTP 500 ", LogRedaction.Describe(new ApiException { ResponseStatusCode = 500 }, includeDetail: true));
        Assert.Equal(string.Empty, LogRedaction.Describe(new InvalidOperationException("x"), includeDetail: true));
    }

    [Fact]
    public void TheAppLogsEveryLineThroughTheRedaction()
    {
        // Services/Log.cs: release builds (no DEBUG_SERVER) take the production redaction and no problem detail.
        var release = Rav1gReleaseTests.Release("Log.cs.txt");
        Assert.Contains("message = LogRedaction.ForProduction(message);", release);
        Assert.Contains("private const bool DebugServerBuild = false;", release);
        Assert.DoesNotContain("LogRedaction.MaskCredentials(message)", release);
        Assert.DoesNotContain("problem.Detail", release);
        var debug = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Log.cs.txt"));
        Assert.Contains("message = LogRedaction.MaskCredentials(message);", debug);
        Assert.Contains("Write(\"ERROR\", $\"{context}: {LogRedaction.Describe(ex, includeDetail: DebugServerBuild)}{ex}\");", debug);
        // Error, Info and Lines all write through Write or mask first.
        Assert.Equal(2, debug.Split("Write(\"").Length - 1);
        Assert.Contains("var masked = messages.Select(LogRedaction.MaskCredentials).ToList();", debug);
        // Artwork logs a URL the redaction recognises, never a bare host.
        var artwork = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Artwork.cs.txt"));
        Assert.DoesNotContain("uri.Host}", artwork);
        Assert.Equal(2, artwork.Split("uri.GetLeftPart(UriPartial.Path)").Length - 1);
    }
}
