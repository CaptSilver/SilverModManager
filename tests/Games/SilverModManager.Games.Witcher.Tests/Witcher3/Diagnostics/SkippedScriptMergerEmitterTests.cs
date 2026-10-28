using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.Witcher.Witcher3;
using SilverModManager.Games.Witcher.Witcher3.Diagnostics;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.Witcher.Tests.Witcher3.Diagnostics;

public class SkippedScriptMergerEmitterTests(ITestOutputHelper helper)
    : ALoadoutDiagnosticEmitterTest<SkippedScriptMergerEmitterTests, Witcher3Game, SkippedScriptMergerEmitter>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddWitcherGames()
            .AddUniversalGameLocator<Witcher3Game>(new Version("4.0"), locationsFileSystem: IsolatedKnownFolders.Create);
    }

    private async Task<LibraryArchive.ReadOnly> AddNamedArchive(string fileName, params string[] paths) =>
        await RegisterLocalArchive(await Witcher3Fixtures.WriteArchive(TemporaryFileManager.CreateFolder().Path / fileName, paths));

    private Task<LoadoutItemGroupId> CreateNexusCollectionGroup(LoadoutId loadout, string name) =>
        Witcher3Fixtures.CreateNexusCollectionGroup(Connection, loadout, name);

    private async Task<LoadoutItemGroup.ReadOnly> Install(LoadoutId loadout, LoadoutItemGroupId parent, string archiveName, params string[] paths)
    {
        var archive = await AddNamedArchive(archiveName, paths);
        var result = await LoadoutManager.InstallItem(archive.AsLibraryFile().AsLibraryItem(), loadout, parent: parent);
        return result.LoadoutItemGroup!.Value;
    }

    [Fact]
    public async Task ASkippedScriptMergerGroup_GetsOneCard()
    {
        var loadout = await CreateLoadout();
        var collection = await CreateNexusCollectionGroup(loadout, "Big list");
        await Install(loadout, collection, "Script Merger.zip", "WitcherScriptMerger/WitcherScriptMerger.exe");

        var diagnostic = await GetSingleDiagnostic(loadout);

        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        await VerifyDiagnostic(diagnostic);
    }

    [Fact]
    public async Task AnEmptyGroupOfAnotherArchive_GetsNoCard()
    {
        var loadout = await CreateLoadout();
        var collection = await CreateNexusCollectionGroup(loadout, "Big list");
        // Rule 4 claims it, but nothing in it matches a placed file, so the group has children; make one with none by hand.
        var archive = await AddNamedArchive("Other.zip", "readme.txt");
        using (var tx = Connection.BeginTransaction())
        {
            var group = new LoadoutItemGroup.New(tx, out var id)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, id) { Name = "Other.zip", LoadoutId = loadout, ParentId = collection },
            };
            _ = new LibraryLinkedLoadoutItem.New(tx, id) { LoadoutItemGroup = group, LibraryItemId = archive.AsLibraryFile().AsLibraryItem() };
            await tx.Commit();
        }

        await ShouldHaveNoDiagnostics(loadout);
    }
}
