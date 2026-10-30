using Bale.Core.Auth;

namespace Bale.Core.Tests;

public class DeviceLoginQrCodeTests
{
    private static DeviceLoginTicket Ticket(string complete = "https://silo.example/activate?code=ABCD1234") =>
        new("device", "ABCD1234", "https://silo.example/activate", complete, DateTimeOffset.UtcNow.AddMinutes(10), TimeSpan.FromSeconds(5));

    [Fact]
    public void EncodesTheCompleteVerificationLinkNotTheDisplayUrl() =>
        Assert.Equal("https://silo.example/activate?code=ABCD1234", DeviceLoginQrCode.Content(Ticket()));

    [Fact]
    public void FallsBackToTheVerificationUriForAnOlderServer() =>
        Assert.Equal("https://silo.example/activate", DeviceLoginQrCode.Content(Ticket(complete: "")));

    [Fact]
    public void RendersAPngWithoutPlatformDrawingApis()
    {
        var png = DeviceLoginQrCode.Png(Ticket());
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        Assert.True(png.Length > 100);
    }
}
