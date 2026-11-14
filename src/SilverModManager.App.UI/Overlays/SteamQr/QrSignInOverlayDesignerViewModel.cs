using SilverModManager.Networking.Steam;

namespace SilverModManager.App.UI.Overlays.SteamQr;

public class QrSignInOverlayDesignerViewModel : AOverlayViewModel<IQrSignInOverlayViewModel>, IQrSignInOverlayViewModel
{
    public Uri Uri { get; } = new("https://s.team/q/1/1234567890");
    public byte[] Image => QrCodeImage.Png(Uri);
    public R3.ReactiveCommand Hide { get; } = new();
}
