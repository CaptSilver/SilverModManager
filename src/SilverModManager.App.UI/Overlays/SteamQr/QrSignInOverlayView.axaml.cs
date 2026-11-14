using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.ReactiveUI;
using ReactiveUI;

namespace SilverModManager.App.UI.Overlays.SteamQr;

public partial class QrSignInOverlayView : ReactiveUserControl<IQrSignInOverlayViewModel>
{
    public QrSignInOverlayView()
    {
        InitializeComponent();
        HeadingTextBlock.Text = "Sign in to Steam";
        InstructionsTextBlock.Text = "Open the Steam mobile app, choose Steam Guard, then scan this code to approve the sign-in.";
        WaitingTextBlock.Text = "Waiting for you to scan the code";
        HideButton.Text = "Hide";

        this.WhenActivated(d =>
        {
            this.WhenAnyValue(v => v.ViewModel)
                .Select(viewModel => viewModel is null ? Observable.Empty<byte[]>() : viewModel.WhenAnyValue(vm => vm.Image))
                .Switch()
                .Subscribe(png =>
                {
                    var previous = QrImage.Source as IDisposable;
                    QrImage.Source = new Bitmap(new MemoryStream(png));
                    previous?.Dispose();
                })
                .DisposeWith(d);

            this.BindCommand<QrSignInOverlayView, IQrSignInOverlayViewModel, ICommand, Button>(ViewModel, vm => vm.Hide, v => v.HideButton)
                .DisposeWith(d);
        });
    }
}
