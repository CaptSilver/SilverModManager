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

    private ModdedLaunchFakes Fakes { get; } = new();

    protected FakeExecutableLocator Executables => Fakes.Executables;
    protected FakeSandboxProbe Probe => Fakes.Probe;
    protected RecordingProcessFactory Processes => Fakes.Processes;
    protected IOSInterop OsInterop => Fakes.OsInterop;
    protected string RuntimeDir => Fakes.RuntimeDir;

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services)
            .OverrideSettingsForTests<FromSoftSettings>(settings => settings with { CoopPassword = Password })
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
        if (seamlessCoop) await Install<SeamlessCoopInstaller>(loadout.LoadoutId, path => ErscRelease.Write(path), "Seamless Co-op.zip");

        await Synchronizer.Synchronize(Refresh(loadout));
        return Refresh(loadout);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Fakes.Dispose();
    }
}
