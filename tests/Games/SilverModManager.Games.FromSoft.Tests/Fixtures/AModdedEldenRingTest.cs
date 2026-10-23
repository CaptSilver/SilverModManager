using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Installers;
using SilverModManager.Games.FromSoft.Overlay;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Settings;
using SilverModManager.StandardGameLocators.TestHelpers;
using NSubstitute;

namespace SilverModManager.Games.FromSoft.Tests.Fixtures;

/// <summary>
/// Elden Ring with a real launch session around fakes: bwrap, the sandbox probe, the user ids and the game process
/// are made up, so the gate and the run tool run for real and nothing is ever started or mounted.
/// </summary>
public abstract class AModdedEldenRingTest<TTest>(ITestOutputHelper helper, bool dirty = false) : AEldenRingTest<TTest>(helper, dirty)
{
    public const string Password = "hunter2";

    private ModdedLaunchFakes Fakes { get; } = new();

    protected FakeExecutableLocator Executables => Fakes.Executables;
    protected FakeSandboxProbe Probe => Fakes.Probe;
    protected RecordingProcessFactory Processes => Fakes.Processes;
    protected IOSInterop OsInterop => Fakes.OsInterop;
    protected string RuntimeDir => Fakes.RuntimeDir;

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services)
            .ReplaceDesktopNotifier();

        Fakes.Register(services);
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
        if (seamlessCoop)
        {
            await Install<SeamlessCoopInstaller>(loadout.LoadoutId, path => ErscRelease.Write(path), "Seamless Co-op.zip");
            await StorePassword();
        }

        await Synchronizer.Synchronize(Refresh(loadout));
        return Refresh(loadout);
    }

    // Written through the host's own store, so the session's transform reads it the way the app does.
    private async Task StorePassword()
    {
        var result = await ServiceProvider.GetRequiredService<ISecretStore>().SetAsync(FromSoftSecretKeys.CoopPassword, Password, CancellationToken.None);
        if (result is not SecretWriteResult.Written)
            throw new InvalidOperationException($"The test host's secret store did not take the co-op password: {result}");
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Fakes.Dispose();
    }
}
