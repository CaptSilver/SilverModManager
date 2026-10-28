using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.Witcher.Witcher3;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.Witcher.Tests.Witcher3;

public abstract class AWitcher3ArchiveTest<TTest>(ITestOutputHelper outputHelper) : ALibraryArchiveInstallerTests<TTest, Witcher3Game>(outputHelper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddWitcherGames()
            .AddUniversalGameLocator<Witcher3Game>(new Version("4.0"), locationsFileSystem: IsolatedKnownFolders.Create);
    }

    /// <summary>Adds a zip called <paramref name="fileName"/> to the library; the file name is the mod's name.</summary>
    protected async Task<LibraryArchive.ReadOnly> AddNamedArchive(string fileName, params string[] paths) =>
        await RegisterLocalArchive(await Witcher3Fixtures.WriteArchive(TemporaryFileManager.CreateFolder().Path / fileName, paths));

    protected Task<LoadoutItemGroupId> CreateNexusCollectionGroup(LoadoutId loadout, string name) =>
        Witcher3Fixtures.CreateNexusCollectionGroup(Connection, loadout, name);

    /// <summary>Installs through the install job, so the game's installers run in order, optionally into a collection group.</summary>
    protected async Task<LoadoutItemGroup.ReadOnly> InstallThroughGame(LoadoutId loadout, LibraryArchive.ReadOnly archive, LoadoutItemGroupId? parent = null)
    {
        var item = archive.AsLibraryFile().AsLibraryItem();
        var result = parent is { } p
            ? await LoadoutManager.InstallItem(item, loadout, parent: p)
            : await LoadoutManager.InstallItem(item, loadout);
        return result.LoadoutItemGroup!.Value;
    }

    protected static string[] Targets(LoadoutItemGroup.ReadOnly group) => group.Children.OfTypeLoadoutItemWithTargetPath()
        .Select(f => ((GamePath)f.TargetPath).ToString())
        .Order(StringComparer.Ordinal)
        .ToArray();
}
