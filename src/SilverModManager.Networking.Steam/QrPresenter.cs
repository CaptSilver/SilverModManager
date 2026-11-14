using SilverModManager.Abstractions.Steam;

namespace SilverModManager.Networking.Steam;

/// <summary>
/// One QR sign-in's code on one handler. Steam replaces the code while it waits for a scan, and each version goes to the
/// handler the login started with. The token handed to the handler is cancelled when the presenter is disposed, which
/// is when the scan finished or the login ended.
/// </summary>
internal sealed class QrPresenter(IAuthInterventionHandler handler, Func<string> challengeUrl) : IDisposable
{
    private readonly CancellationTokenSource _shown = new();

    public void Show() => handler.ShowQRCode(new Uri(challengeUrl(), UriKind.Absolute), _shown.Token);

    public void Dispose()
    {
        // Not disposed: a refresh callback already in flight may still read the token.
        _shown.Cancel();
    }
}
