using QRCoder;

namespace Bale.Core.Auth;

/// <summary>Portable PNG rendering: no System.Drawing, reflection or platform image encoder.</summary>
public static class DeviceLoginQrCode
{
    /// <summary>
    /// The official TvLoginViewModel.kt encodes verificationUriComplete, including the user code,
    /// not the display-only /activate address. The base URI is a fallback for older servers.
    /// </summary>
    public static string Content(DeviceLoginTicket ticket) =>
        string.IsNullOrWhiteSpace(ticket.VerificationUriComplete) ? ticket.VerificationUri : ticket.VerificationUriComplete;

    public static byte[] Png(DeviceLoginTicket ticket)
    {
        using var data = QRCodeGenerator.GenerateQrCode(Content(ticket), QRCodeGenerator.ECCLevel.M);
        using var png = new PngByteQRCode(data);
        return png.GetGraphic(6);
    }
}
