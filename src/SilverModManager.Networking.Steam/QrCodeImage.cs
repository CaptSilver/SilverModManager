using QRCoder;

namespace SilverModManager.Networking.Steam;

/// <summary>
/// A QR code as a PNG, for a window that shows it as a picture. The terminal prints its own ASCII version of the same code.
/// </summary>
public static class QrCodeImage
{
    /// <summary>The PNG bytes of a QR code for <paramref name="uri"/>, dark modules on a white background with a quiet zone, so phone cameras read it on a dark theme.</summary>
    public static byte[] Png(Uri uri, int pixelsPerModule = 8)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(uri.ToString(), QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(pixelsPerModule);
    }
}
