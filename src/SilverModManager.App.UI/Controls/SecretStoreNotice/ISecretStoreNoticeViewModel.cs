using System.Reactive;
using SilverModManager.UI.Sdk;
using ReactiveUI;

namespace SilverModManager.App.UI.Controls.SecretStoreNotice;

public enum SecretStoreNoticeKind
{
    None,
    Unencrypted,
    Locked,
}

public interface ISecretStoreNoticeViewModel : IViewModelInterface
{
    SecretStoreNoticeKind Kind { get; }

    /// <summary>
    /// The sentence shown for <see cref="Kind"/>; empty when there is nothing to say.
    /// </summary>
    string Text { get; }

    /// <summary>
    /// <see cref="Kind"/> is not <see cref="SecretStoreNoticeKind.None"/> and the user has not dismissed the notice.
    /// </summary>
    bool IsVisible { get; }

    ReactiveCommand<Unit, Unit> UnlockCommand { get; }

    ReactiveCommand<Unit, Unit> DismissCommand { get; }
}
