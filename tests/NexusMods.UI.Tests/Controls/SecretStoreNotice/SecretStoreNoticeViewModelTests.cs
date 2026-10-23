using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Abstractions.Secrets;
using NexusMods.App.UI.Controls.Alerts;
using NexusMods.App.UI.Controls.SecretStoreNotice;
using NexusMods.App.UI.Settings;
using NexusMods.Sdk.Settings;
using R3;
using ReactiveUI;

namespace NexusMods.UI.Tests.Controls.SecretStoreNotice;

public class SecretStoreNoticeViewModelTests(IServiceProvider provider) : AUiTest(provider)
{
    private const string Key = "secrets-unencrypted";
    private const string Path = "/home/test/.local/share/SilverModManager/secrets.json";

    private static SecretStoreStatus Plain(bool locked = false, bool unencrypted = false, string detail = "Memory") =>
        new(SecretBackendKind.Memory, detail, locked, unencrypted, Writable: true, []);

    private ISettingsManager Settings => Provider.GetRequiredService<ISettingsManager>();

    private async Task<SecretStoreNoticeViewModel> Build(ISecretStore store)
    {
        // The settings manager outlives the test, so start from a clean dismissal.
        Settings.Update<AlertSettings>(s => s with { AlertStatus = s.AlertStatus.SetItem(Key, false) });
        return await OnUi(() => Task.FromResult(new SecretStoreNoticeViewModel(store, Settings, NullLogger<SecretStoreNoticeViewModel>.Instance)));
    }

    private async Task<IDisposable> Activate(SecretStoreNoticeViewModel vm) =>
        await OnUi(() => Task.FromResult(vm.Activator.Activate()));

    [Fact]
    public async Task Unencrypted_IsVisible_AndNamesTheFile()
    {
        var store = new InMemorySecretStore { Status = Plain(unencrypted: true, detail: Path) };
        var vm = await Build(store);
        using var _ = await Activate(vm);

        await EventuallyOnUi(() =>
        {
            vm.Kind.Should().Be(SecretStoreNoticeKind.Unencrypted);
            vm.IsVisible.Should().BeTrue();
            vm.Text.Should().Contain(Path).And.Contain("unencrypted");
        });
    }

    [Fact]
    public async Task Dismiss_Hides_AndPersistsInAlertSettings()
    {
        var store = new InMemorySecretStore { Status = Plain(unencrypted: true, detail: Path) };
        var vm = await Build(store);
        using var _ = await Activate(vm);
        await EventuallyOnUi(() => vm.IsVisible.Should().BeTrue());

        await OnUi(() => vm.DismissCommand.Execute().Subscribe());

        await EventuallyOnUi(() => vm.IsVisible.Should().BeFalse());
        Settings.Get<AlertSettings>().IsDismissed(Key).Should().BeTrue();
    }

    [Fact]
    public async Task ADismissalFromAnEarlierRun_StaysHidden()
    {
        var store = new InMemorySecretStore { Status = Plain(unencrypted: true, detail: Path) };
        var vm = await Build(store);
        Settings.Update<AlertSettings>(s => s with { AlertStatus = s.AlertStatus.SetItem(Key, true) });
        var restarted = await OnUi(() => Task.FromResult(new SecretStoreNoticeViewModel(store, Settings, NullLogger<SecretStoreNoticeViewModel>.Instance)));
        using var _ = await Activate(restarted);

        await EventuallyOnUi(() => restarted.Kind.Should().Be(SecretStoreNoticeKind.Unencrypted));
        restarted.IsVisible.Should().BeFalse();
    }

    [Fact]
    public async Task FallingBackAgainAfterADismissal_ShowsTheNoticeAgain()
    {
        var store = new InMemorySecretStore { Status = Plain() };
        var vm = await Build(store);
        using var _ = await Activate(vm);
        await EventuallyOnUi(() => vm.Kind.Should().Be(SecretStoreNoticeKind.None));

        store.Status = Plain(unencrypted: true, detail: Path);
        await EventuallyOnUi(() => vm.IsVisible.Should().BeTrue());
        await OnUi(() => vm.DismissCommand.Execute().Subscribe());
        await EventuallyOnUi(() => vm.IsVisible.Should().BeFalse());

        store.Status = Plain();
        await EventuallyOnUi(() => vm.Kind.Should().Be(SecretStoreNoticeKind.None));
        store.Status = Plain(unencrypted: true, detail: Path);

        await EventuallyOnUi(() => vm.IsVisible.Should().BeTrue());
        Settings.Get<AlertSettings>().IsDismissed(Key).Should().BeFalse();
    }

    [Fact]
    public async Task ARunWithAWorkingKeyring_ForgetsAnEarlierDismissal()
    {
        var healthy = new InMemorySecretStore { Status = Plain() };
        await Build(healthy);
        Settings.Update<AlertSettings>(s => s with { AlertStatus = s.AlertStatus.SetItem(Key, true) });
        var keyringRun = await OnUi(() => Task.FromResult(new SecretStoreNoticeViewModel(healthy, Settings, NullLogger<SecretStoreNoticeViewModel>.Instance)));
        using (await Activate(keyringRun))
        {
            await EventuallyOnUi(() => Settings.Get<AlertSettings>().IsDismissed(Key).Should().BeFalse());
        }

        var fallback = new InMemorySecretStore { Status = Plain(unencrypted: true, detail: Path) };
        var fileRun = await OnUi(() => Task.FromResult(new SecretStoreNoticeViewModel(fallback, Settings, NullLogger<SecretStoreNoticeViewModel>.Instance)));
        using var _ = await Activate(fileRun);

        await EventuallyOnUi(() => fileRun.IsVisible.Should().BeTrue());
    }

    [Fact]
    public async Task Locked_ShowsTheLockedText_AndUnlocksOnceOnActivation()
    {
        var store = new InMemorySecretStore { Status = Plain(locked: true) };
        var vm = await Build(store);
        using var _ = await Activate(vm);

        await EventuallyOnUi(() =>
        {
            vm.Kind.Should().Be(SecretStoreNoticeKind.Locked);
            vm.IsVisible.Should().BeTrue();
            vm.Text.Should().Contain("keyring is locked");
            store.Calls.Count(c => c == "Unlock").Should().Be(1);
        });
    }

    [Fact]
    public async Task TheUnlockCommand_CallsTheStore_AndAnUnlockedStatusHidesTheNotice()
    {
        var store = new InMemorySecretStore { Status = Plain(locked: true) };
        var vm = await Build(store);
        using var _ = await Activate(vm);
        await EventuallyOnUi(() => store.Calls.Count(c => c == "Unlock").Should().Be(1));

        await OnUi(() => vm.UnlockCommand.Execute().Subscribe());
        await EventuallyOnUi(() => store.Calls.Count(c => c == "Unlock").Should().Be(2));

        store.Status = Plain(locked: false);
        await EventuallyOnUi(() =>
        {
            vm.Kind.Should().Be(SecretStoreNoticeKind.None);
            vm.IsVisible.Should().BeFalse();
        });
    }

    [Fact]
    public async Task AHealthyStore_ShowsNothing_AndDoesNotUnlock()
    {
        var store = new InMemorySecretStore { Status = Plain() };
        var vm = await Build(store);
        using var _ = await Activate(vm);

        await EventuallyOnUi(() => store.Calls.Should().Contain("Describe"));
        vm.IsVisible.Should().BeFalse();
        vm.Text.Should().BeEmpty();
        store.Calls.Should().NotContain("Unlock");
    }

    [Fact]
    public async Task ASlowStore_DoesNotBlockActivation()
    {
        var inner = new InMemorySecretStore { Status = Plain(unencrypted: true, detail: Path) };
        using var gate = new ManualResetEventSlim();
        var vm = await Build(new SlowDescribeStore(inner, gate));

        try
        {
            // Describe stays blocked until the gate opens, so activation can only finish if it did not wait for it.
            using var _ = await Activate(vm).WaitAsync(TimeSpan.FromSeconds(10));
            await OnUi(() => vm.Kind.Should().Be(SecretStoreNoticeKind.None));

            gate.Set();
            await EventuallyOnUi(() => vm.Kind.Should().Be(SecretStoreNoticeKind.Unencrypted), maxTimeout: TimeSpan.FromSeconds(10));
        }
        finally
        {
            gate.Set();
        }
    }

    private sealed class SlowDescribeStore(ISecretStore inner, ManualResetEventSlim gate) : ISecretStore
    {
        public ValueTask<SecretResult> GetAsync(SecretKey key, CancellationToken ct) => inner.GetAsync(key, ct);
        public ValueTask<SecretWriteResult> SetAsync(SecretKey key, string value, CancellationToken ct) => inner.SetAsync(key, value, ct);
        public ValueTask<SecretWriteResult> DeleteAsync(SecretKey key, CancellationToken ct) => inner.DeleteAsync(key, ct);
        public ValueTask<IReadOnlyList<SecretKey>> ListAsync(CancellationToken ct) => inner.ListAsync(ct);
        public ValueTask<SecretStoreStatus> UnlockAsync(CancellationToken ct) => inner.UnlockAsync(ct);
        public Observable<SecretKey> Changed => inner.Changed;
        public Observable<SecretStoreStatus> StatusChanged => inner.StatusChanged;

        // Blocks the calling thread on purpose: a view model that called this on the UI thread would stall it.
        public ValueTask<SecretStoreStatus> DescribeAsync(CancellationToken ct)
        {
            gate.Wait(TimeSpan.FromSeconds(30));
            return inner.DescribeAsync(ct);
        }
    }
}
