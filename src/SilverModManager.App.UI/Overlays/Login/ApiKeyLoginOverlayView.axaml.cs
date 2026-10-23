using Avalonia.ReactiveUI;
using NexusMods.App.UI.Resources;
using R3;
using ReactiveUI;

namespace NexusMods.App.UI.Overlays.Login;

public partial class ApiKeyLoginOverlayView : ReactiveUserControl<IApiKeyLoginOverlayViewModel>
{
    /// <summary>
    /// The page on Nexus Mods where a personal API key is created.
    /// </summary>
    private const string KeyPageUrl = "https://www.nexusmods.com/users/myaccount?tab=api";

    public ApiKeyLoginOverlayView()
    {
        InitializeComponent();

        TitleText.Text = Language.ApiKeyLoginOverlayView_Title;
        DescriptionText.Text = Language.ApiKeyLoginOverlayView_Description;
        KeyPageUrlText.Text = KeyPageUrl;
        ApiKeyTextBox.Watermark = Language.ApiKeyLoginOverlayView_Watermark;
        ButtonCancel.Text = Language.ApiKeyLoginOverlayView_Cancel;
        ButtonSave.Text = Language.ApiKeyLoginOverlayView_Save;

        this.WhenActivated(disposables =>
        {
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
        });
    }
}
