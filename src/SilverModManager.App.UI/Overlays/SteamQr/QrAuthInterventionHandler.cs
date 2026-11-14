using Avalonia.Threading;
using JetBrains.Annotations;
using SilverModManager.Abstractions.Steam;

namespace SilverModManager.App.UI.Overlays.SteamQr;

/// <summary>
/// Shows Steam's sign-in QR code in an overlay of the window, and takes it down when the code is no longer wanted.
/// It is the registered handler of the window's process; a verb run from a terminal passes its own handler instead.
/// </summary>
[UsedImplicitly]
public sealed class QrAuthInterventionHandler(IOverlayController overlays) : IAuthInterventionHandler
{
    private readonly Action<Action> _onUi = static action => Dispatcher.UIThread.Post(action);

    // Only touched on the UI thread.
    private QrSignInOverlayViewModel? _current;
    private CancellationToken _currentToken;

    /// <summary>For tests, which run no Avalonia dispatcher: where the work that must happen on the UI thread runs.</summary>
    internal QrAuthInterventionHandler(IOverlayController overlays, Action<Action> onUi) : this(overlays) => _onUi = onUi;

    public void ShowQRCode(Uri uri, CancellationToken token)
    {
        // SteamKit calls from its own threads.
        _onUi(() => Present(uri, token));
        // Registered outside the UI hop so a token cancelled before the hop runs still closes what the hop opens.
        token.Register(() => _onUi(() => Dismiss(token)));
    }

    private void Present(Uri uri, CancellationToken token)
    {
        if (token.IsCancellationRequested) return;

        // Steam swaps the code for a fresh one while it waits; that is the same sign-in, so the picture changes in place.
        // A person who hid the overlay is not shown it again for every fresh code.
        if (_current is { } open && _currentToken == token)
        {
            if (open.Status != Status.Closed) open.Show(uri);
            return;
        }

        _current?.Close();
        var overlay = new QrSignInOverlayViewModel(uri);
        _current = overlay;
        _currentToken = token;
        overlays.Enqueue(overlay);
    }

    private void Dismiss(CancellationToken token)
    {
        if (_current is null || _currentToken != token) return;
        _current.Close();
        _current = null;
    }
}
