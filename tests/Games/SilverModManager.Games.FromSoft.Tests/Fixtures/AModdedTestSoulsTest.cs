using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.FromSoft.Installers;
using SilverModManager.Games.FromSoft.Overlay;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.TestFramework.Helpers;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Settings;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.FromSoft.Tests.Fixtures;

/// <summary>
/// Test Souls installed from a Steam library, with a real launch session around fakes. The only FromSoft game these
/// tests register is Test Souls, so everything they find was read from its definition.
/// </summary>
public abstract class AModdedTestSoulsTest<TTest>(ITestOutputHelper helper) : ASteamInstalledGameTest<TTest, TestSoulsGame>(helper)
{
    private ModdedLaunchFakes Fakes { get; } = new();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services).AddTestSouls().ReplaceDesktopNotifier();
        Fakes.Register(services);
        return services;
    }

    protected override SteamInstall InstallGame(SteamLibraryFixture library) => TestSoulsInstall.Install(library);

    /// <summary>The <c>Bin</c> subfolder of the install, which no sync may write to.</summary>
    protected AbsolutePath RealGameFolder => FromSoftPaths.GameFolder(TestSoulsGame.Definition.Layout, GameInstallation);

    protected FromSoftSettings Settings => ServiceProvider.GetRequiredService<ISettingsManager>().Get<FromSoftSettings>();

    protected OverlayLaunchGate Gate => ServiceProvider.GetServices<ILaunchGate>().OfType<OverlayLaunchGate>().Single();

    protected OverlayLayout LayoutOf(Loadout.ReadOnly loadout)
        => OverlayLayout.For(Settings, ServiceProvider.GetRequiredService<IFileSystem>(), GameInstallation, loadout.LoadoutId);

    protected LaunchContext ContextOf(Loadout.ReadOnly loadout) => new(GameInstallation, loadout.LoadoutId, loadout.Name, []);

    protected async Task AddModWithFiles(Loadout.ReadOnly loadout, string name, params GamePath[] paths)
    {
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, name);
        foreach (var path in paths)
        {
            var bytes = Encoding.UTF8.GetBytes($"{name}:{path.Path}");
            AddFile(tx, loadout.LoadoutId, group, path, $"{name}:{path.Path}");
            await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(path.Path, new MemoryStream(bytes)), bytes.xxHash3(), Size.FromLong(bytes.Length))]);
        }

        await tx.Commit();
    }

    private async Task Install<TInstaller>(LoadoutId loadout, Func<AbsolutePath, Task> write, string archiveName)
        where TInstaller : ILibraryItemInstaller
    {
        var path = TemporaryFileManager.CreateFolder().Path / archiveName;
        await write(path);
        var archive = await RegisterLocalArchive(path);
        await LoadoutManager.InstallItem(archive.AsLibraryFile().AsLibraryItem(), loadout, installer: Game.LibraryItemInstallers.OfType<TInstaller>().Single());
    }

    /// <summary>A loadout with the me3 release and one package, applied, so the host and the mods are on disk.</summary>
    protected async Task<Loadout.ReadOnly> CreateModdedLoadout()
    {
        var loadout = await CreateLoadout();
        await Install<Me3HostInstaller>(loadout.LoadoutId,
            path => TestArchives.WriteTarGz(path, "bin/me3", "bin/win64/me3-launcher.exe", "bin/win64/me3_mod_host.dll"), "me3-linux-amd64.tar.gz");
        await Install<Me3PackageInstaller>(loadout.LoadoutId, path => TestArchives.Write(path, "parts/wp_a.dcx"), "Weapons.zip");

        await Synchronizer.Synchronize(Refresh(loadout));
        return Refresh(loadout);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Fakes.Dispose();
    }
}
