using SilverModManager.Abstractions.Cli;
using SilverModManager.Abstractions.Steam;
using SilverModManager.Sdk.ProxyConsole;
using QRCoder;

namespace SilverModManager.Networking.Steam.CLI;

/// <summary>
/// Shows the sign-in QR code as text on the terminal of the verb that started the login. It is built per verb over that
/// verb's renderer and passed to <see cref="ISteamSession.Connect(IAuthInterventionHandler, CancellationToken)"/>; it is
/// never the registered default, because the process hosting a forwarded verb is the window's.
/// </summary>
public class RenderingAuthenticationHandler(IRenderer renderer) : IAuthInterventionHandler
{
    private Task _rendering = Task.CompletedTask;

    /// <summary>The render started by the latest code, for a caller that has to wait for it to reach the terminal.</summary>
    internal Task Rendering => Volatile.Read(ref _rendering);

    public void ShowQRCode(Uri uri, CancellationToken token)
    {
        var previous = Rendering;
        // One chain, so a refreshed code is never printed ahead of the one before it.
        var next = Task.Run(async () =>
            {
                await previous.ConfigureAwait(false);
                await renderer.RenderAsync(Renderable.Text("Please scan the QR code with your Steam Mobile App to continue the authentication process."));
                using var qrGenerator = new QRCodeGenerator();
                using var qrCodeData = qrGenerator.CreateQrCode(uri.ToString(), QRCodeGenerator.ECCLevel.L);
                using var qrCode = new AsciiQRCode(qrCodeData);
                var asciiArt = qrCode.GetGraphic(1, drawQuietZones: false);
                var lines = $"\n\n{asciiArt}\n\n";
                await renderer.RenderAsync(Renderable.Text(lines));
            }
        );
        Volatile.Write(ref _rendering, next);
    }
}
