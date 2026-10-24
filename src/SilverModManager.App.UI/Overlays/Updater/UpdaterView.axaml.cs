using System.Reactive.Linq;
using Avalonia.ReactiveUI;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Resources;
using SilverModManager.Sdk;
using ReactiveUI;
using R3;

namespace SilverModManager.App.UI.Overlays.Updater;

public partial class UpdaterView : ReactiveUserControl<IUpdaterViewModel>
{
    public UpdaterView()
    {
        InitializeComponent();

        this.WhenActivated(disposables =>
            {
                this.WhenAnyValue(view => view.ViewModel)
                    .WhereNotNull()
                    .SubscribeWithErrorLogging(vm =>
                        {
                            ButtonUpdateNow.Text = Language.Updater_UpdateNow;
                            ButtonRestartNow.Text = Language.Updater_RestartNow;
                            // The browser download stays for installs that cannot update themselves.
                            ButtonDownloadReleaseAssetInBrowser.IsVisible = vm.HasAsset && !vm.CanUpdateInApp;
                            HeadingText.Text = string.Format(Language.Updater_UpdateAvailable, vm.LatestVersion.ToString(fieldCount: 3));
                            TextGenericBody.Text = string.Format(Language.Updater_GenericMessage, vm.CurrentVersion.ToString(fieldCount: 3), vm.LatestVersion.ToString(fieldCount: 3));

                            var installationMethod = vm.InstallationMethod;

                            if (installationMethod is InstallationMethod.PackageManager)
                            {
                                TextInstructions.Text = Language.Updater_UsePackageManager;
                            }
                            else if (installationMethod is InstallationMethod.Flatpak)
                            {
                                TextInstructions.Text = Language.Updater_UseFlatpak;
                            }
                            else
                            {
                                TextInstructions.IsVisible = false;
                            }

                            // we need to change styling of the open browser button depending on if we have an asset or not
                            ButtonOpenReleaseInBrowser.Type = vm.HasAsset ? StandardButton.Types.Tertiary : StandardButton.Types.Primary;
                            ButtonOpenReleaseInBrowser.Fill = vm.HasAsset ? StandardButton.Fills.Weak : StandardButton.Fills.Strong;
                        }
                    )
                    .AddTo(disposables);

                this.BindCommand(ViewModel, vm => vm.CommandClose, view => view.ButtonClose)
                    .AddTo(disposables);

                this.BindCommand(ViewModel, vm => vm.CommandOpenReleaseInBrowser, view => view.ButtonOpenReleaseInBrowser)
                    .AddTo(disposables);

                this.BindCommand(ViewModel, vm => vm.CommandDownloadReleaseAssetInBrowser, view => view.ButtonDownloadReleaseAssetInBrowser)
                    .AddTo(disposables);

                this.BindCommand(ViewModel, vm => vm.CommandUpdateNow, view => view.ButtonUpdateNow)
                    .AddTo(disposables);

                this.BindCommand(ViewModel, vm => vm.CommandRestartNow, view => view.ButtonRestartNow)
                    .AddTo(disposables);

                this.WhenAnyValue(view => view.ViewModel)
                    .WhereNotNull()
                    .Select(vm => vm.WhenAnyValue(x => x.State, x => x.CanUpdateInApp))
                    .Switch()
                    .SubscribeWithErrorLogging(tuple =>
                        {
                            var (state, canUpdate) = tuple;
                            ButtonUpdateNow.IsVisible = canUpdate && state is UpdateState.Available or UpdateState.Failed;
                            ButtonRestartNow.IsVisible = state is UpdateState.ReadyToRestart;
                            DownloadProgress.IsVisible = state is UpdateState.Downloading;
                            ButtonClose.Text = state is UpdateState.ReadyToRestart ? Language.Updater_Later : "Close";
                        }
                    )
                    .AddTo(disposables);

                this.OneWayBind(ViewModel, vm => vm.Progress, view => view.DownloadProgress.Value)
                    .AddTo(disposables);

                this.OneWayBind(ViewModel, vm => vm.StatusMessage, view => view.TextStatus.Text)
                    .AddTo(disposables);

                this.OneWayBind(ViewModel, vm => vm.StatusMessage, view => view.TextStatus.IsVisible, message => !string.IsNullOrEmpty(message))
                    .AddTo(disposables);
            }
        );
    }
}
