using R3;
using ReactiveUI.SourceGenerators;
using SilverModManager.Networking.Steam;

namespace SilverModManager.App.UI.Overlays.SteamQr;

public partial class QrSignInOverlayViewModel : AOverlayViewModel<IQrSignInOverlayViewModel>, IQrSignInOverlayViewModel
{
    public QrSignInOverlayViewModel(Uri uri)
    {
        Uri = uri;
        Image = QrCodeImage.Png(uri);
        Hide = new R3.ReactiveCommand(execute: _ => Close());
    }

    [Reactive] public partial Uri Uri { get; private set; }
    [Reactive] public partial byte[] Image { get; private set; }

    public R3.ReactiveCommand Hide { get; }

    /// <summary>Steam replaced the code while the overlay was open; the picture follows.</summary>
    public void Show(Uri uri)
    {
        Uri = uri;
        Image = QrCodeImage.Png(uri);
    }
}
