using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.TestFramework;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.FromSoft.Tests.Fixtures;

/// <summary>Runs one of Elden Ring's installers on a test archive and reports where its files would land.</summary>
public abstract class AEldenRingInstallerTests<TTest>(ITestOutputHelper outputHelper) : ALibraryArchiveInstallerTests<TTest, EldenRingGame>(outputHelper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddFromSoftGamesWithOwnDataRoot()
            .AddUniversalGameLocator<EldenRingGame>(new Version("1.0"));
    }

    protected async Task<(InstallerResult Result, string[] Targets)> TryInstall<TInstaller>(LibraryArchive.ReadOnly archive)
        where TInstaller : ILibraryArchiveInstaller
    {
        var loadout = await CreateLoadout();
        var installer = Game.LibraryItemInstallers.OfType<TInstaller>().Single();

        using var tx = Connection.BeginTransaction();
        var libraryItem = archive.AsLibraryFile().AsLibraryItem();
        var group = new LoadoutItemGroup.New(tx, out var groupId)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, groupId) { Name = libraryItem.Name, LoadoutId = loadout },
        };

        var result = await installer.ExecuteAsync(libraryItem, group, tx, loadout, CancellationToken.None);
        if (!result.IsSuccess) return (result, []);

        var committed = await tx.Commit();
        var targets = committed.Remap(group).Children
            .Select(child => child.TryGetAsLoadoutItemWithTargetPath(out var item) ? (GamePath)item.TargetPath : throw new InvalidOperationException("The child should have a target path."))
            .Select(path => $"{path.LocationId}:{path.Path}")
            .ToArray();
        return (result, targets);
    }

    /// <summary>An archive with the given name, so the package id the installer derives is known.</summary>
    protected async Task<LibraryArchive.ReadOnly> AddNamed(string archiveName, params string[] paths)
    {
        var path = TemporaryFileManager.CreateFolder().Path / archiveName;
        await TestArchives.Write(path, paths);
        return await RegisterLocalArchive(path);
    }

    protected static string Me3Mods(string path) => $"{LocationId.Me3Mods}:{path}";
}
