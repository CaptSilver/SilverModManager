using System.Reactive.Disposables;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.App.UI.Controls.Alerts;
using SilverModManager.Sdk.Settings;
using SilverModManager.UI.Sdk;
using R3;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using ReactiveCommand = ReactiveUI.ReactiveCommand;
using Unit = System.Reactive.Unit;

namespace SilverModManager.App.UI.Controls.SecretStoreNotice;

/// <summary>
/// Tells the user when sign-in tokens sit in a plain file or the keyring is locked, and offers to unlock it.
/// Every store call runs on the thread pool: the store has its own timeouts, but a locked wallet can still
/// take seconds to answer and the window must stay responsive meanwhile.
/// </summary>
public class SecretStoreNoticeViewModel : AViewModel<ISecretStoreNoticeViewModel>, ISecretStoreNoticeViewModel
{
    internal const string DismissKey = "secrets-unencrypted";

    internal const string LockedText = "Your keyring is locked; you are signed out of Nexus Mods until it is unlocked.";

    private readonly ISecretStore _store;
    private readonly ILogger<SecretStoreNoticeViewModel> _logger;
    private readonly AlertSettingsWrapper _alert;

    [Reactive] public SecretStoreNoticeKind Kind { get; private set; }
    [Reactive] public string Text { get; private set; } = string.Empty;
    [Reactive] public bool IsVisible { get; private set; }

    public ReactiveUI.ReactiveCommand<Unit, Unit> UnlockCommand { get; }
    public ReactiveUI.ReactiveCommand<Unit, Unit> DismissCommand { get; }

    public SecretStoreNoticeViewModel(ISecretStore store, ISettingsManager settings, ILogger<SecretStoreNoticeViewModel> logger)
    {
        _store = store;
        _logger = logger;
        _alert = new AlertSettingsWrapper(settings, DismissKey);

        UnlockCommand = ReactiveCommand.CreateFromTask(async () => Apply(await Task.Run(() => _store.UnlockAsync(CancellationToken.None).AsTask())));
        DismissCommand = ReactiveCommand.Create(() =>
        {
            _alert.DismissAlert();
            Refresh();
        });

        this.WhenActivated(d =>
        {
            var cts = new CancellationTokenSource();
            System.Reactive.Disposables.Disposable.Create(cts.Cancel).DisposeWith(d);
            cts.DisposeWith(d);

            _store.StatusChanged
                .ObserveOnUIThreadDispatcher()
                .Subscribe(Apply)
                .DisposeWith(d);

            _ = Task.Run(() => DescribeAndMaybeUnlockAsync(cts.Token));
        });
    }

    private async Task DescribeAndMaybeUnlockAsync(CancellationToken token)
    {
        try
        {
            var status = await _store.DescribeAsync(token);
            await Dispatcher.UIThread.InvokeAsync(() => Apply(status));

            // The boot prime never prompts, so this is where the wallet dialog comes from. Once per activation.
            if (status.Locked)
            {
                var unlocked = await _store.UnlockAsync(token);
                await Dispatcher.UIThread.InvokeAsync(() => Apply(unlocked));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception e)
        {
            _logger.LogError(e, "Reading the secret store status failed");
        }
    }

    // Always on the UI thread.
    private void Apply(SecretStoreStatus status)
    {
        var kind = status.Locked ? SecretStoreNoticeKind.Locked
            : status.Unencrypted ? SecretStoreNoticeKind.Unencrypted
            : SecretStoreNoticeKind.None;

        // A working keyring forgets the dismissal, in this run or a later one, so the next fallback to the
        // file is shown again instead of hiding behind an old click.
        if (kind == SecretStoreNoticeKind.None && _alert.IsDismissed)
            _alert.ShowAlert();

        Kind = kind;
        Text = kind switch
        {
            SecretStoreNoticeKind.Unencrypted => $"Sign-in tokens are stored unencrypted in {status.Detail} because no keyring answered. Unlock or install a keyring and restart to move them.",
            SecretStoreNoticeKind.Locked => LockedText,
            _ => string.Empty,
        };
        Refresh();
    }

    private void Refresh() => IsVisible = Kind != SecretStoreNoticeKind.None && !_alert.IsDismissed;
}
