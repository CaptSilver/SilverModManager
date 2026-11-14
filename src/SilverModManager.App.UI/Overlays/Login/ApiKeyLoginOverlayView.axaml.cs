using Avalonia.ReactiveUI;
using SilverModManager.App.UI.Resources;
using R3;
using ReactiveUI;

namespace SilverModManager.App.UI.Overlays.Login;

public partial class ApiKeyLoginOverlayView : ReactiveUserControl<IApiKeyLoginOverlayViewModel>
{
    public ApiKeyLoginOverlayView()
    {
        InitializeComponent();

        ButtonCancel.Text = Language.ApiKeyLoginOverlayView_Cancel;
        ButtonSave.Text = Language.ApiKeyLoginOverlayView_Save;

        this.WhenActivated(disposables =>
        {
            this.OneWayBind(ViewModel, vm => vm.Title.Value, view => view.TitleText.Text)
                .AddTo(disposables);

            this.OneWayBind(ViewModel, vm => vm.Instructions.Value, view => view.DescriptionText.Text)
                .AddTo(disposables);

            this.OneWayBind(ViewModel, vm => vm.PageUrl.Value, view => view.ButtonOpenPage.Text)
                .AddTo(disposables);

            this.OneWayBind(ViewModel, vm => vm.PageUrl.Value, view => view.ButtonOpenPage.IsVisible, url => !string.IsNullOrEmpty(url))
                .AddTo(disposables);

            this.OneWayBind(ViewModel, vm => vm.Watermark.Value, view => view.ApiKeyTextBox.Watermark)
                .AddTo(disposables);

            this.Bind(ViewModel, vm => vm.ApiKey.Value, view => view.ApiKeyTextBox.Text)
                .AddTo(disposables);

            this.OneWayBind(ViewModel, vm => vm.ErrorMessage.Value, view => view.ErrorText.Text)
                .AddTo(disposables);

            this.OneWayBind(ViewModel, vm => vm.ErrorMessage.Value, view => view.ErrorText.IsVisible, message => !string.IsNullOrEmpty(message))
                .AddTo(disposables);

            this.BindCommand(ViewModel, vm => vm.CommandSave, view => view.ButtonSave)
                .AddTo(disposables);

            this.BindCommand(ViewModel, vm => vm.CommandCancel, view => view.ButtonCancel)
                .AddTo(disposables);

            this.BindCommand(ViewModel, vm => vm.CommandOpenPage, view => view.ButtonOpenPage)
                .AddTo(disposables);
        });
    }
}
