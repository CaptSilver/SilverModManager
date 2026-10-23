using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.FromSoft.Doctor;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Installers;
using SilverModManager.Games.FromSoft.Me3;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using SilverModManager.Games.TestFramework;
using NexusMods.Paths;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.FromSoft.Tests.Fixtures;

/// <summary>Elden Ring with a fake GitHub source in place of the real one, so no test reaches the network.</summary>
public abstract class Me3ProvisioningTest<TTest>(ITestOutputHelper helper)
    : ALoadoutDiagnosticEmitterTest<TTest, EldenRingGame, Me3HostMissingEmitter>(helper)
{
    private static readonly string[] ReleaseMembers = ["bin/me3", "bin/win64/me3-launcher.exe", "bin/win64/me3_mod_host.dll"];

    protected FakeSource Source { get; } = new();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services)
            .AddFromSoftGamesWithOwnDataRoot()
            .AddUniversalGameLocator<EldenRingGame>(new Version("1.0"));
        services.Replace(ServiceDescriptor.Singleton<IGitHubReleaseAssetSource>(Source));
        // The host starts every hosted service, so without this a package installed by a test would start a fetch of its own.
        if (!AutoInstall) services.Replace(ServiceDescriptor.Singleton<Me3AutoInstaller>(provider => new IdleAutoInstaller(provider)));
        return services;
    }

    protected abstract bool AutoInstall { get; }

    private sealed class IdleAutoInstaller(IServiceProvider provider) : Me3AutoInstaller(
        provider.GetRequiredService<IJobMonitor>(), provider.GetRequiredService<IConnection>(), provider.GetRequiredService<Me3HostProvisioner>(),
        provider.GetRequiredService<ILogger<Me3AutoInstaller>>())
    {
        public override Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    protected Me3HostProvisioner Provisioner => ServiceProvider.GetRequiredService<Me3HostProvisioner>();

    protected async Task<LibraryArchive.ReadOnly> AddReleaseTarball(string name = "me3-linux-amd64.tar.gz")
    {
        var path = TemporaryFileManager.CreateFolder().Path / name;
        await TestArchives.WriteTarGz(path, ReleaseMembers);
        return await RegisterLocalArchive(path);
    }

    protected async Task InstallPackage(LoadoutId loadout)
    {
        var path = TemporaryFileManager.CreateFolder().Path / "Weapons.zip";
        await TestArchives.Write(path, "parts/wp_a.dcx");
        var archive = await RegisterLocalArchive(path);
        await LoadoutManager.InstallItem(archive.AsLibraryFile().AsLibraryItem(), loadout,
            installer: Game.LibraryItemInstallers.OfType<Me3PackageInstaller>().Single());
    }

    protected int HostFileCount(LoadoutId loadout)
        => LoadoutItem.FindByLoadout(Connection.Db, loadout).OfTypeLoadoutItemWithTargetPath()
            .Count(item => (GamePath)item.TargetPath == new GamePath(LocationId.Me3Host, Me3HostFiles.Binary));

    protected static async Task Eventually(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("The condition did not hold within 30 seconds.");
            await Task.Delay(25);
        }
    }

    protected sealed class FakeSource : IGitHubReleaseAssetSource
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public Func<Task<LibraryArchive.ReadOnly>> Result { get; set; } = () => throw new InvalidOperationException("No release was expected to be fetched.");

        public async ValueTask<LibraryFile.ReadOnly> AddLatestAssetAsync(ulong repositoryId, string suffix, string? nameHint, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            repositoryId.Should().Be(540883721UL);
            suffix.Should().Be("linux-amd64.tar.gz");
            return (await Result()).AsLibraryFile();
        }
    }
}
