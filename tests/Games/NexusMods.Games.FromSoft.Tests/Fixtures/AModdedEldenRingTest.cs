using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.FromSoft.Installers;
using NexusMods.Games.FromSoft.Overlay;
using NexusMods.Paths;
using NexusMods.Platform.Proton.Launch;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.Settings;
using NexusMods.StandardGameLocators.TestHelpers;
using NSubstitute;

namespace NexusMods.Games.FromSoft.Tests.Fixtures;

/// <summary>
/// Elden Ring with a real launch session around fakes: bwrap, the sandbox probe, the user ids and the game process
/// are made up, so the gate and the run tool run for real and nothing is ever started or mounted.
/// </summary>
public abstract class AModdedEldenRingTest<TTest>(ITestOutputHelper helper, bool dirty = false) : AEldenRingTest<TTest>(helper, dirty)
{
    public const string Password = "hunter2";

    protected FakeExecutableLocator Executables { get; } = new();
    protected FakeSandboxProbe Probe { get; } = new();
    protected RecordingProcessFactory Processes { get; } = new();
    protected IOSInterop OsInterop { get; } = Substitute.For<IOSInterop>();
    protected string RuntimeDir { get; } = Directory.CreateTempSubdirectory("silvermod-eldenring-lock-").FullName;

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services)
            .OverrideSettingsForTests<FromSoftSettings>(settings => settings with { CoopPassword = Password })
            .ReplaceDesktopNotifier();

        services.Replace(ServiceDescriptor.Singleton<IExecutableLocator>(Executables));
        services.Replace(ServiceDescriptor.Singleton<ISandboxProbe>(Probe));
        services.Replace(ServiceDescriptor.Singleton<IUserIds>(new FixedUserIds(1000, 1001)));
        services.Replace(ServiceDescriptor.Singleton<IGameProcessFactory>(Processes));
        services.Replace(ServiceDescriptor.Singleton<IOSInterop>(OsInterop));
        services.Replace(ServiceDescriptor.Singleton<IInstallationLockFactory>(new TempLockFactory(RuntimeDir)));
        services.Replace(ServiceDescriptor.Singleton<ILaunchSynchronizer, NoSynchronizer>());
        services.Replace(ServiceDescriptor.Singleton<IGameLaunchSession>(provider => provider.GetRequiredService<ActiveLaunchSessions>()));
        services.AddSingleton(provider => new ActiveLaunchSessions(provider));
        services.AddTransient<LaunchSession>();
        return services;
    }

    protected OverlayLaunchGate Gate => ServiceProvider.GetServices<ILaunchGate>().OfType<OverlayLaunchGate>().Single();

    protected OverlayLayout LayoutOf(Loadout.ReadOnly loadout)
        => OverlayLayout.For(ServiceProvider.GetRequiredService<ISettingsManager>().Get<FromSoftSettings>(), ServiceProvider.GetRequiredService<IFileSystem>(), GameInstallation, loadout.LoadoutId);

    protected LaunchContext ContextOf(Loadout.ReadOnly loadout) => new(GameInstallation, loadout.LoadoutId, loadout.Name, []);

    private async Task Install<TInstaller>(LoadoutId loadout, Func<AbsolutePath, Task> write, string archiveName)
        where TInstaller : ILibraryItemInstaller
    {
        var path = TemporaryFileManager.CreateFolder().Path / archiveName;
        await write(path);
        var archive = await RegisterLocalArchive(path);
        await LoadoutManager.InstallItem(archive.AsLibraryFile().AsLibraryItem(), loadout, installer: Game.LibraryItemInstallers.OfType<TInstaller>().Single());
    }

    /// <summary>A loadout with the me3 release and one package, applied, so the host and the mods are on disk.</summary>
    protected async Task<Loadout.ReadOnly> CreateModdedLoadout(bool seamlessCoop = false)
    {
        var loadout = await CreateLoadout();
        await Install<Me3HostInstaller>(loadout.LoadoutId,
            path => TestArchives.WriteTarGz(path, "bin/me3", "bin/win64/me3-launcher.exe", "bin/win64/me3_mod_host.dll"), "me3-linux-amd64.tar.gz");
        await Install<Me3PackageInstaller>(loadout.LoadoutId, path => TestArchives.Write(path, "parts/wp_a.dcx"), "Weapons.zip");
        if (seamlessCoop) await Install<SeamlessCoopInstaller>(loadout.LoadoutId, path => ErscRelease.Write(path), "Seamless Co-op.zip");

        await Synchronizer.Synchronize(Refresh(loadout));
        return Refresh(loadout);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Directory.Delete(RuntimeDir, recursive: true);
    }

    protected sealed class FakeExecutableLocator : IExecutableLocator
    {
        public AbsolutePath? Bwrap { get; set; } = NexusMods.Paths.FileSystem.Shared.FromUnsanitizedFullPath("/fake/bin/bwrap");

        public AbsolutePath? Find(string name) => name == "bwrap" ? Bwrap : null;
    }

    protected sealed class FakeSandboxProbe : ISandboxProbe
    {
        public string? Failure { get; set; }
        public int Calls { get; private set; }

        public string? Probe(AbsolutePath bwrap)
        {
            Calls++;
            return Failure;
        }
    }

    private sealed record FixedUserIds(uint Uid, uint Gid) : IUserIds;

    private sealed class TempLockFactory(string runtimeDir) : IInstallationLockFactory
    {
        public InstallationLock Create(string store, string storeId) => new(InstallationLock.PathFor(runtimeDir, store, storeId));
    }

    private sealed class NoSynchronizer : ILaunchSynchronizer
    {
        public Task Synchronize(LoadoutId loadoutId) => Task.CompletedTask;
    }

    /// <summary>Records what it was asked to start and "runs" it by calling <see cref="OnStart"/>.</summary>
    protected sealed class RecordingProcessFactory : IGameProcessFactory
    {
        public List<GameProcessSpec> Started { get; } = [];
        public Action? OnStart { get; set; }

        public IGameProcess Start(GameProcessSpec spec, GameInstallation installation)
        {
            lock (Started) Started.Add(spec);
            OnStart?.Invoke();
            return new FinishedProcess();
        }

        public Task<TrackedProcess?> ShutDownGameAsync(GameInstallation installation, CancellationToken cancellationToken) => Task.FromResult<TrackedProcess?>(null);

        private sealed class FinishedProcess : IGameProcess
        {
            public int Pid => 4242;
            public Task<GameProcessResult> WaitAsync(CancellationToken cancellationToken = default) => Task.FromResult(new GameProcessResult(0, null));
            public void Terminate() { }
            public void Dispose() { }
        }
    }
}
