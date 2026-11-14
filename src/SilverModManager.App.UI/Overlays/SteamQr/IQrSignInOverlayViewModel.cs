namespace SilverModManager.App.UI.Overlays.SteamQr;

public interface IQrSignInOverlayViewModel : IOverlayViewModel
{
    /// <summary>The address Steam wants the mobile app to open; it changes while the overlay waits for a scan.</summary>
    Uri Uri { get; }

    /// <summary>The code for <see cref="Uri"/> as a PNG.</summary>
    byte[] Image { get; }

    /// <summary>Hides the overlay. The sign-in itself keeps waiting for a scan.</summary>
    R3.ReactiveCommand Hide { get; }
}
