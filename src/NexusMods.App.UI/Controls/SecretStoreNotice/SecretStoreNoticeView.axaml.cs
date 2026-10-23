using System.Reactive.Disposables;
using Avalonia.ReactiveUI;
using JetBrains.Annotations;
using ReactiveUI;

namespace NexusMods.App.UI.Controls.SecretStoreNotice;

[UsedImplicitly]
public partial class SecretStoreNoticeView : ReactiveUserControl<ISecretStoreNoticeViewModel>
{
    public SecretStoreNoticeView()
    {
        InitializeComponent();

        this.WhenActivated(d =>
        {
            this.OneWayBind(ViewModel, vm => vm.IsVisible, view => view.IsVisible)
                .DisposeWith(d);

            this.OneWayBind(ViewModel, vm => vm.Text, view => view.NoticeAlert.Body)
                .DisposeWith(d);

            this.OneWayBind(ViewModel, vm => vm.Kind, view => view.NoticeAlert.Title, kind => kind switch
                {
                    SecretStoreNoticeKind.Locked => "Keyring locked",
                    SecretStoreNoticeKind.Unencrypted => "Sign-in tokens are not encrypted",
                    _ => string.Empty,
                })
                .DisposeWith(d);

            this.OneWayBind(ViewModel, vm => vm.Kind, view => view.UnlockButton.IsVisible, kind => kind == SecretStoreNoticeKind.Locked)
                .DisposeWith(d);

            this.BindCommand(ViewModel, vm => vm.UnlockCommand, view => view.UnlockButton)
                .DisposeWith(d);

            this.BindCommand(ViewModel, vm => vm.DismissCommand, view => view.DismissButton)
                .DisposeWith(d);
        });
    }
}
