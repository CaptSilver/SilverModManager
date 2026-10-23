using System.Reactive.Disposables;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.ReactiveUI;
using ReactiveUI;

namespace SilverModManager.App.UI.Overlays.Login;

public partial class NexusLoginOverlayView : ReactiveUserControl<INexusLoginOverlayViewModel>
{
    public NexusLoginOverlayView()
    {
        InitializeComponent();
        this.WhenActivated(d =>
        {
            CopyButton.Command = ReactiveCommand.CreateFromTask(async () =>
            {
                await TopLevel.GetTopLevel(this)!.Clipboard!.SetTextAsync(ViewModel?.Uri?.ToString());
            });

            this.BindCommand<NexusLoginOverlayView, INexusLoginOverlayViewModel, ICommand, Button>(ViewModel, vm => vm.Cancel, v => v.CancelButton)
                .DisposeWith(d);

            UseApiKeyButton.Text = SilverModManager.App.UI.Resources.Language.NexusLoginOverlayView_Use_API_key_instead;

            this.BindCommand<NexusLoginOverlayView, INexusLoginOverlayViewModel, ICommand, Button>(ViewModel, vm => vm.UseApiKey, v => v.UseApiKeyButton)
                .DisposeWith(d);

            this.OneWayBind(ViewModel, vm => vm.Uri, view => view.UrlTextBlock.Text)
                .DisposeWith(d);
        });
    }
}

