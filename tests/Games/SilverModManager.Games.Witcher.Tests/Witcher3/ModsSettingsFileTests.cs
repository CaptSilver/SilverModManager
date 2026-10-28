using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.Witcher.Witcher3;
using SilverModManager.Games.Witcher.Witcher3.Models;
using SilverModManager.Games.Witcher.Witcher3.SortOrder;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.Witcher.Tests.Witcher3;

public class ModsSettingsFileTests(ITestOutputHelper helper)
    : AIsolatedGameTest<ModsSettingsFileTests, Witcher3Game>(helper)
{
    private const string Merged = "mod0000_MergedFiles";

    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddWitcherGames()
        .AddUniversalGameLocator<Witcher3Game>(new Version("4.0"), locationsFileSystem: IsolatedKnownFolders.Create);

    private WitcherModSortOrderVariety Variety =>
        Game.SortOrderManager.GetSortOrderVarieties().OfType<WitcherModSortOrderVariety>().Single();

    private IIntrinsicFile File(Loadout.ReadOnly loadout) =>
        ((ALoadoutSynchronizer)Game.Synchronizer).IntrinsicFiles(loadout)[WitcherConstants.W3ModsSettings];

    private Loadout.ReadOnly Fresh(Loadout.ReadOnly loadout) => Loadout.Load(Connection.Db, loadout.LoadoutId);

    private async Task<Loadout.ReadOnly> LoadoutWithOrder()
    {
        var loadout = await CreateLoadout();
        await Variety.GetOrCreateSortOrderFor(loadout.LoadoutId, loadout.LoadoutId);
        return loadout;
    }

    private async Task<LoadoutItemGroupId> AddMod(Loadout.ReadOnly loadout, string name, ulong priority, params string[] paths)
    {
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, name);
        foreach (var path in paths) AddFile(tx, loadout.LoadoutId, group, new GamePath(LocationId.Game, path), path);
        _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadout.LoadoutId, TargetId = group, Priority = ConflictPriority.From(priority) };
        var result = await tx.Commit();
        return LoadoutItemGroupId.From(result[group.Value]);
    }

    private async Task<string> Written(Loadout.ReadOnly loadout)
    {
        using var stream = new MemoryStream();
        await File(Fresh(loadout)).Write(stream, Fresh(loadout), new Dictionary<GamePath, SyncNode>());
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static Dictionary<GamePath, SyncNode> TreeWithPrevious() => new()
    {
        [WitcherConstants.W3ModsSettings] = new SyncNode
        {
            Previous = new SyncNodePart { Hash = Hash.From(1), Size = NexusMods.Paths.Size.From(10), LastModifiedTicks = 0 },
        },
    };

    private async Task Ingest(Loadout.ReadOnly loadout, byte[] onDisk, Dictionary<GamePath, SyncNode>? tree = null)
    {
        loadout = Fresh(loadout);
        using var tx = Connection.BeginTransaction();
        await File(loadout).Ingest(new MemoryStream(onDisk), loadout, tree ?? TreeWithPrevious(), tx);
        await tx.Commit();
    }

    private Task Ingest(Loadout.ReadOnly loadout, string onDisk) => Ingest(loadout, new UTF8Encoding(false).GetBytes(onDisk));

    private WitcherModsSettingsNotice.ReadOnly[] Notices() => WitcherModsSettingsNotice.All(Connection.Db).ToArray();

    private static string[] Sections(string text) => ModsSettingsDocument.Parse(text).Select(s => s.Folder).ToArray();

    [Fact]
    public async Task Write_RendersTheLoadoutOrder()
    {
        var loadout = await LoadoutWithOrder();
        await AddMod(loadout, "A", 2, "Mods/modA/a.ws");
        await AddMod(loadout, "B", 1, "Mods/modB/b.ws");
        var id = Variety.GetSortOrderIdFor(loadout.LoadoutId, Connection.Db).Value;

        await Variety.MoveItems(id, [new SortItemKey<string>("modb")], new SortItemKey<string>("moda"), TargetRelativePosition.BeforeTarget, Connection.Db);

        ModsSettingsDocument.Parse(await Written(loadout)).Select(s => (s.Folder, s.Priority))
            .Should().Equal((Merged, 1), ("modB", 2), ("modA", 3));
    }

    [Fact]
    public async Task Write_WithNoMods_HasOnlyTheMergedSection()
    {
        var loadout = await LoadoutWithOrder();

        Sections(await Written(loadout)).Should().Equal(Merged);
    }

    [Fact]
    public async Task Write_WritesOneSectionForTwoFoldersThatDifferOnlyByCase()
    {
        var loadout = await LoadoutWithOrder();
        await AddMod(loadout, "SdlLow", 1, "Mods/modSDL/a.ws");
        await AddMod(loadout, "SdlHigh", 2, "Mods/ModSdl/b.ws");

        var sections = Sections(await Written(loadout));

        sections.Should().HaveCount(2);
        sections.Count(s => s.Equals("modsdl", StringComparison.OrdinalIgnoreCase)).Should().Be(1);
    }

    [Fact]
    public async Task Write_WritesASwitchedOffFolderAsDisabled()
    {
        var loadout = await LoadoutWithOrder();
        await AddMod(loadout, "A", 1, "Mods/modA/a.ws");
        var id = Variety.GetSortOrderIdFor(loadout.LoadoutId, Connection.Db).Value;
        await Variety.SetEnabled(id, new SortItemKey<string>("moda"), false);

        ModsSettingsDocument.Parse(await Written(loadout)).Single(s => s.Folder == "modA").Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task Ingest_RecordsAVortexWrittenFileAsANotice()
    {
        var loadout = await LoadoutWithOrder();
        await AddMod(loadout, "A", 1, "Mods/modA/a.ws");
        await AddMod(loadout, "B", 2, "Mods/modB/b.ws");
        var before = Variety.StoredOrder(Fresh(loadout));

        await Ingest(loadout, "[mod0000_MergedFiles]\nEnabled=1\nPriority=1\nVK=\n[modA]\nEnabled=1\nPriority=2\nVK=modA-1-1-1\n[modB]\nEnabled=1\nPriority=3\nVK=modB-2-1-1\n");

        var notice = Notices().Should().ContainSingle().Subject;
        notice.Summary.Should().Contain("modA").And.Contain("modB");
        Variety.StoredOrder(Fresh(loadout)).Should().Equal(before, "the loadout is not rewritten from the file");
    }

    [Fact]
    public async Task Ingest_ARecognisedFileThatMatchesRecordsNothing()
    {
        var loadout = await LoadoutWithOrder();
        await AddMod(loadout, "A", 1, "Mods/modA/a.ws");

        await Ingest(loadout, await Written(loadout));

        Notices().Should().BeEmpty();
    }

    [Fact]
    public async Task Ingest_WithoutAPreviousWrite_RecordsNothing()
    {
        var loadout = await LoadoutWithOrder();
        await AddMod(loadout, "A", 1, "Mods/modA/a.ws");

        await Ingest(loadout, Encoding.UTF8.GetBytes("[modZ]\nPriority=1\n"), new Dictionary<GamePath, SyncNode>());

        Notices().Should().BeEmpty();
    }

    [Fact]
    public async Task Ingest_RecordsAnUnreadableFileAsANotice()
    {
        var loadout = await LoadoutWithOrder();
        await AddMod(loadout, "A", 1, "Mods/modA/a.ws");

        await Ingest(loadout, [0, 0]);

        Notices().Should().ContainSingle().Which.Summary.Should().Contain("could not be read");
    }

    [Fact]
    public async Task Ingest_AVortexFileWithDuplicatePrioritiesAndAMissingFolder_IsReplacedByTheNextWrite()
    {
        var loadout = await LoadoutWithOrder();
        await AddMod(loadout, "A", 1, "Mods/modA/a.ws");
        await AddMod(loadout, "B", 2, "Mods/modB/b.ws");
        const string vortex = "[mod0000_MergedFiles]\nEnabled=1\nPriority=1\n[modA]\nEnabled=1\nPriority=2\nVK=modA\n[modB]\nEnabled=0\nPriority=2\nVK=modB\n[modGone]\nEnabled=1\nPriority=4\nVK=modGone\n";

        var ingest = async () => await Ingest(loadout, vortex);

        await ingest.Should().NotThrowAsync();
        var notice = Notices().Should().ContainSingle().Subject;
        notice.Summary.Should().Contain("modGone").And.Contain("modB");
        var next = await Written(loadout);
        Sections(next).Should().Equal(Merged, "modB", "modA");
        next.Should().NotContain("modGone").And.NotContain("VK").And.Contain("Enabled=1");
    }

    [Fact]
    public async Task Ingest_AFileWithNoSections_NamesTheMissingFolders()
    {
        var loadout = await LoadoutWithOrder();
        await AddMod(loadout, "A", 1, "Mods/modA/a.ws");

        await Ingest(loadout, "");

        Notices().Should().ContainSingle().Which.Summary.Should().Contain("modA");
    }

    [Fact]
    public async Task Ingest_ASecondDifferingFile_ReplacesTheNoticeInsteadOfAddingOne()
    {
        var loadout = await LoadoutWithOrder();
        await AddMod(loadout, "A", 1, "Mods/modA/a.ws");
        await Ingest(loadout, "[modZ]\nPriority=1\n");

        await Ingest(loadout, "[modY]\nPriority=1\n");

        Notices().Should().ContainSingle().Which.Summary.Should().Contain("modY").And.NotContain("modZ");
    }

    [Fact]
    public async Task Ingest_ThenAMatchingFile_ClearsTheNotice()
    {
        var loadout = await LoadoutWithOrder();
        await AddMod(loadout, "A", 1, "Mods/modA/a.ws");
        await Ingest(loadout, "[modZ]\nPriority=1\n");

        await Ingest(loadout, await Written(loadout));

        Notices().Should().BeEmpty();
    }

    [Fact]
    public void Synchronizer_ListsModsSettingsAsIntrinsic()
    {
        var synchronizer = (ALoadoutSynchronizer)Game.Synchronizer;

        synchronizer.IntrinsicFiles(default).Keys.Should().ContainSingle().Which.Should().Be(WitcherConstants.W3ModsSettings);
    }

    [Fact]
    public async Task RenderForReset_KeepsOnlyTheFoldersThatStayOnDisk()
    {
        var loadout = await LoadoutWithOrder();
        var path = Fresh(loadout).InstallationInstance.Locations.ToAbsolutePath(WitcherConstants.W3ModsSettings);
        path.Parent.CreateDirectory();
        await path.WriteAllTextAsync("[mod0000_MergedFiles]\nEnabled=1\nPriority=1\n[modApp]\nEnabled=1\nPriority=2\n[modGame]\nEnabled=0\nPriority=5\n");
        var desired = new Dictionary<GamePath, SyncNode>
        {
            [new GamePath(LocationId.Game, "Mods/modGame/g.ws")] = new() { Loadout = new SyncNodePart { Hash = Hash.From(1), Size = NexusMods.Paths.Size.From(1), LastModifiedTicks = 0 } },
            [new GamePath(LocationId.Game, "Mods/modApp/a.ws")] = new() { Disk = new SyncNodePart { Hash = Hash.From(2), Size = NexusMods.Paths.Size.From(1), LastModifiedTicks = 0 } },
        };

        var bytes = await ((IIntrinsicFileWithReset)File(Fresh(loadout))).RenderForReset(Fresh(loadout), desired);

        var sections = ModsSettingsDocument.Parse(Encoding.UTF8.GetString(bytes!));
        sections.Select(s => (s.Folder, s.Enabled, s.Priority)).Should().Equal(("modGame", false, 1));
    }

    [Fact]
    public async Task RenderForReset_WithNoFileOnDisk_LeavesItAlone()
    {
        var loadout = await LoadoutWithOrder();
        Fresh(loadout).InstallationInstance.Locations.ToAbsolutePath(WitcherConstants.W3ModsSettings).Delete();

        var bytes = await ((IIntrinsicFileWithReset)File(Fresh(loadout))).RenderForReset(Fresh(loadout), new Dictionary<GamePath, SyncNode>());

        bytes.Should().BeNull();
    }

    [Fact]
    public async Task Apply_WritesTheFileForAModInstalledJustBefore()
    {
        var loadout = await CreateLoadout();
        await AddMod(loadout, "A", 1, "Mods/modA/a.ws");

        await Synchronizer.Synchronize(Fresh(loadout));

        var onDisk = Fresh(loadout).InstallationInstance.Locations.ToAbsolutePath(WitcherConstants.W3ModsSettings);
        Sections(await onDisk.ReadAllTextAsync()).Should().Equal(Merged, "modA");
    }

    [Fact]
    public async Task Apply_ReplacesAVortexFileAndReportsIt()
    {
        var loadout = await CreateLoadout();
        await AddMod(loadout, "A", 1, "Mods/modA/a.ws");
        await Synchronizer.Synchronize(Fresh(loadout));
        var onDisk = Fresh(loadout).InstallationInstance.Locations.ToAbsolutePath(WitcherConstants.W3ModsSettings);
        await onDisk.WriteAllTextAsync("[mod0000_MergedFiles]\nEnabled=1\nPriority=1\nVK=x\n[modA]\nEnabled=0\nPriority=1\nVK=modA-1-1-1\n[modGone]\nEnabled=1\nPriority=9\nVK=g\n");

        await Synchronizer.Synchronize(Fresh(loadout));

        Notices().Should().ContainSingle().Which.Summary.Should().Contain("modGone").And.Contain("modA");
        var text = await onDisk.ReadAllTextAsync();
        Sections(text).Should().Equal(Merged, "modA");
        text.Should().NotContain("VK");
    }
}
