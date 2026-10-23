using System.Reactive;
using SilverModManager.UI.Sdk;
using ReactiveUI;

namespace SilverModManager.App.UI.Controls.SecretStoreNotice;

public class SecretStoreNoticeDesignViewModel : AViewModel<ISecretStoreNoticeViewModel>, ISecretStoreNoticeViewModel
{
    public SecretStoreNoticeKind Kind => SecretStoreNoticeKind.Locked;

    public string Text => SecretStoreNoticeViewModel.LockedText;

    public bool IsVisible => true;

    public ReactiveCommand<Unit, Unit> UnlockCommand { get; } = ReactiveCommand.Create(() => { });

    public ReactiveCommand<Unit, Unit> DismissCommand { get; } = ReactiveCommand.Create(() => { });
}
