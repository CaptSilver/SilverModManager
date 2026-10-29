using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Sdk.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.Witcher.Witcher3;
using SilverModManager.Games.Witcher.Witcher3.Diagnostics;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.Witcher.Tests.Witcher3.Diagnostics;

public class UnplacedArchiveFilesEmitterTests(ITestOutputHelper helper)
    : ALoadoutDiagnosticEmitterTest<UnplacedArchiveFilesEmitterTests, Witcher3Game, UnplacedArchiveFilesEmitter>(helper)
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

    private async Task Install(LoadoutId loadout, string archiveName, LoadoutItemGroupId? parent, params string[] paths)
    {
        var archive = await AddNamedArchive(archiveName, paths);
        var item = archive.AsLibraryFile().AsLibraryItem();
        if (parent is { } p) await LoadoutManager.InstallItem(item, loadout, parent: p);
        else await LoadoutManager.InstallItem(item, loadout);
    }

    [Fact]
    public async Task AFileNoRulePlaced_GetsACard()
    {
        var loadout = await CreateLoadout();
        await Install(loadout, "Example Mod.zip", null, "content/scripts/d.ws", "tools/patch.exe");

        var diagnostic = await GetSingleDiagnostic(loadout);

        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        await VerifyDiagnostic(diagnostic);
    }

    [Fact]
    public async Task TheCheckPlansOverThePathsInOrdinalOrderLikeTheInstaller()
    {
        // Two roots both holding the menu folder: the mod root is the first one found, which only agrees with the
        // installer when the paths are walked in ordinal order (Z before a), not the archive's own order.
        const string Menu = "bin/config/r4game/user_config_matrix/pc/sdl.xml";
        var loadout = await CreateLoadout();
        await Install(loadout, "Two roots.zip", null, $"Zroot/{Menu}", $"aroot/{Menu}", "aroot/extra.bin");

        var card = (await GetSingleDiagnostic(loadout)).FormatSummary(new PlainDiagnosticWriter());

        card.Should().Contain("aroot/extra.bin");
    }

    [Fact]
    public async Task DocumentationAndParts_GetNoCard()
    {
        var loadout = await CreateLoadout();
        await Install(loadout, "Plain.zip", null, "content/scripts/d.ws", "readme.txt", "screenshot.png");
        await Install(loadout, "Menu.zip", null, "modSDL/bin/config/r4game/user_config_matrix/pc/sdl.xml", "modSDL/input.settings.part.txt");

        await ShouldHaveNoDiagnostics(loadout);
    }

    [Fact]
    public async Task ALocalisationCsvBesideTheContent_GetsNoCard()
    {
        var loadout = await CreateLoadout();
        await Install(loadout, "Standardised Naming.zip", null, "modNaming/content/scripts/d.ws", "en.csv");

        await ShouldHaveNoDiagnostics(loadout);
    }

    [Fact]
    public async Task TheSkippedScriptMergerArchive_GetsNoUnplacedCards()
    {
        var loadout = await CreateLoadout();
        var collection = await CreateNexusCollectionGroup(loadout, "Big list");
        await Install(loadout, "Script Merger.zip", collection, "WitcherScriptMerger/WitcherScriptMerger.exe", "WitcherScriptMerger/lib.dll");

        await ShouldHaveNoDiagnostics(loadout);
    }

    [Fact]
    public async Task ASubsetGroupNoRuleInstalled_GetsNoCard()
    {
        var loadout = await CreateLoadout();
        var archive = await AddNamedArchive("Subset.zip", "content/a.ws", "content/b.ws", "tools/patch.exe");
        var kept = archive.Children.Single(c => c.Path.Path == "content/a.ws");
        using (var tx = Connection.BeginTransaction())
        {
            var group = new LoadoutItemGroup.New(tx, out var id)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, id) { Name = "Subset.zip", LoadoutId = loadout },
            };
            _ = new LibraryLinkedLoadoutItem.New(tx, id) { LoadoutItemGroup = group, LibraryItemId = archive.AsLibraryFile().AsLibraryItem() };
            var fileId = tx.TempId();
            _ = new LoadoutFile.New(tx, fileId)
            {
                LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(tx, fileId)
                {
                    TargetPath = (loadout.Id, LocationId.Game, (RelativePath)"Mods/modSubset/content/a.ws"),
                    LoadoutItem = new LoadoutItem.New(tx, fileId) { Name = "a.ws", LoadoutId = loadout, ParentId = group.Id },
                },
                Hash = kept.AsLibraryFile().Hash,
                Size = kept.AsLibraryFile().Size,
            };
            await tx.Commit();
        }

        await ShouldHaveNoDiagnostics(loadout, "the group is not what the Witcher 3 rules produce, so the user or a collection chose the subset");
    }

    [Fact]
    public async Task TheBackupCopyOfAMenuXml_GetsNoCard()
    {
        var loadout = await CreateLoadout();
        await Install(loadout, "Menu.zip", null,
            "modX/bin/config/r4game/user_config_matrix/pc/x.xml", "modX/backup/bin/config/r4game/user_config_matrix/pc/x.xml");

        await ShouldHaveNoDiagnostics(loadout);
    }
}
