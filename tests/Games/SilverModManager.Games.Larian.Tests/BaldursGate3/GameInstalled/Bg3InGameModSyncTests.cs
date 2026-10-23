using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Abstractions.Loadouts.Synchronizers.Rules;
using NexusMods.App.UI.DiagnosticSystem;
using NexusMods.Games.Larian.BaldursGate3;
using NexusMods.Games.Larian.BaldursGate3.Emitters;
using NexusMods.Games.Larian.BaldursGate3.GameInstalled;
using NexusMods.Games.Larian.BaldursGate3.ModList;
using NexusMods.Games.Larian.BaldursGate3.Models;
using NexusMods.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.StandardGameLocators.TestHelpers;

namespace NexusMods.Games.Larian.Tests.BaldursGate3.GameInstalled;

/// <summary>
/// Paks the game downloaded from mod.io, sitting in the Mods folder of an isolated prefix, go through a real
/// sync: they are recorded as game-installed items, and never backed up or listed as external changes.
/// </summary>
public class Bg3InGameModSyncTests(ITestOutputHelper helper)
    : AIsolatedGameTest<Bg3InGameModSyncTests, Larian.BaldursGate3.BaldursGate3>(helper)
{
    private const string RecordedPak = "mymod-ab12.pak";
    private const string PatternPak = "other_mod-cd34.pak";
    private const string HandDroppedPak = "plain-ef56.pak";
    private const string MissingUuid = "33333333-3333-4333-8333-333333333333";
    private const string RecordUuid = "11111111-2222-3333-4444-555555555555";
    private const string RecordMd5 = "0123456789abcdef0123456789abcdef";

    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddBaldursGate3()
        .AddDiagnosticWriter()
        .AddUniversalGameLocator<Larian.BaldursGate3.BaldursGate3>(new Version("1.61"), locationsFileSystem: IsolatedKnownFolders.Create);

    private AbsolutePath OnDisk(GamePath path) => GameInstallation.Locations.ToAbsolutePath(path);

    private static GamePath Pak(string name) => new(Bg3Constants.ModsLocationId, name);

    private async Task WritePak(string name, byte[] bytes)
    {
        var file = OnDisk(Pak(name));
        file.Parent.CreateDirectory();
        await file.WriteAllBytesAsync(bytes);
    }

    private async Task WriteModsettings(params string[] entries)
    {
        var nodes = string.Concat(entries);
        var file = OnDisk(new GamePath(Bg3Constants.PlayerProfilesLocationId, "modsettings.lsx"));
        file.Parent.CreateDirectory();
        await file.WriteAllTextAsync($"""
            <?xml version="1.0" encoding="UTF-8"?>
            <save>
                <version major="4" minor="8" revision="0" build="400"/>
                <region id="ModuleSettings"><node id="root"><children><node id="Mods"><children>
            {nodes}
                </children></node></children></node></region>
            </save>
            """);
    }

    private static string RecordedEntry(string folder, string name, string handle, string md5 = RecordMd5) => $"""
        <node id="ModuleShortDesc">
            <attribute id="Folder" type="LSString" value="{folder}"/>
            <attribute id="MD5" type="LSString" value="{md5}"/>
            <attribute id="Name" type="LSString" value="{name}"/>
            <attribute id="PublishHandle" type="uint64" value="{handle}"/>
            <attribute id="UUID" type="guid" value="{RecordUuid}"/>
            <attribute id="Version64" type="int64" value="{SyntheticPak.Version(1, 2, 3)}"/>
        </node>
        """;

    private async Task<Loadout.ReadOnly> Fixture()
    {
        var loadout = await CreateLoadout();
        await WritePak(RecordedPak, SyntheticPak.ForPublishedModule("MyMod", "My Mod", RecordUuid, SyntheticPak.Version(1, 2, 3), "4242"));
        await WritePak(PatternPak, SyntheticPak.ForPublishedModule("OtherMod", "Other Mod", MissingUuid, SyntheticPak.Version(2), "9000"));
        await WritePak(HandDroppedPak, SyntheticPak.ForModule("Plain", "Plain", MissingUuid, SyntheticPak.Version(1)));
        await WriteModsettings(RecordedEntry("MyMod", "My Mod", "4242"));
        return loadout;
    }

    private async Task<Loadout.ReadOnly> Apply(Loadout.ReadOnly loadout) => await Synchronizer.Synchronize(Refresh(loadout));

    private GameInstalledItem.ReadOnly[] Items(Loadout.ReadOnly loadout) =>
        GameInstalledGroup.FindByInstalledFor(Connection.Db, loadout.Id)
            .SelectMany(group => group.AsLoadoutItemGroup().Children.OfTypeLoadoutItemGroup().OfTypeGameInstalledItem())
            .ToArray();

    private GameInstalledItem.ReadOnly Item(Loadout.ReadOnly loadout, string pak) => Items(loadout).Single(i => i.SourceId == pak);

    private string[] OverridesPaths(Loadout.ReadOnly loadout) =>
        LoadoutOverridesGroup.FindByOverridesFor(Connection.Db, loadout.Id)
            .SelectMany(group => group.AsLoadoutItemGroup().Children.OfTypeLoadoutItemWithTargetPath())
            .Select(item => ((GamePath)item.TargetPath).Path.ToString())
            .ToArray();

    private static Hash HashOf(byte[] bytes) => bytes.xxHash3();

    private async Task<Hash> DiskHash(string pak) => HashOf(await OnDisk(Pak(pak)).ReadAllBytesAsync());

    [Fact]
    public async Task ARecordMatchedPakIsAnItemThatKeepsItsModuleEntry()
    {
        var loadout = await Fixture();

        await Apply(loadout);

        var item = Item(loadout, RecordedPak);
        item.Confidence.Should().Be("GameRecord");
        item.TryGetAsBg3ModuleEntry(out var entry).Should().BeTrue();
        entry.Folder.Value.Should().Be("MyMod");
        entry.Uuid.Value.Should().Be(RecordUuid);
        entry.Version64.Value.Should().Be(SyntheticPak.Version(1, 2, 3).ToString());
        entry.PublishHandle.Value.Should().Be("4242");
        entry.Md5.Value.Should().Be(RecordMd5);
        entry.ModuleName.Value.Should().Be("My Mod");
    }

    private TxId[] ItemTransactions(Loadout.ReadOnly loadout) =>
        Connection.Db.Datoms(Item(Refresh(loadout), RecordedPak).Id).Select(d => d.T).Order().ToArray();

    [Fact]
    public async Task AChangedModuleEntryUpdatesTheStoredEntryOnTheNextSync()
    {
        var loadout = await Fixture();
        await Apply(loadout);

        // Only the checksum differs: the pak, its title and its files are as before.
        const string newMd5 = "fedcba9876543210fedcba9876543210";
        await WriteModsettings(RecordedEntry("MyMod", "My Mod", "4242", newMd5));
        await Apply(loadout);

        Item(loadout, RecordedPak).TryGetAsBg3ModuleEntry(out var entry).Should().BeTrue();
        entry.Md5.Value.Should().Be(newMd5);
    }

    [Fact]
    public async Task APatternMatchedPakIsAnItemWithoutAModuleEntry()
    {
        var loadout = await Fixture();

        await Apply(loadout);

        var item = Item(loadout, PatternPak);
        item.Confidence.Should().Be("FileNamePattern");
        item.TryGetAsBg3ModuleEntry(out _).Should().BeFalse();
    }

    [Fact]
    public async Task AnUnchangedEntryAddsNoDatomsOnTheNextSync()
    {
        var loadout = await Fixture();
        await Apply(loadout);
        var before = ItemTransactions(loadout);

        // A new pak makes the next sync rewrite the group, the recorded item and its entry included.
        await WritePak("newer-ab99.pak", SyntheticPak.ForPublishedModule("Newer", "Newer", MissingUuid, SyntheticPak.Version(1), "9002"));
        await Apply(loadout);

        Items(loadout).Select(i => i.SourceId).Should().Contain("newer-ab99.pak", "the sync did rewrite the group");
        ItemTransactions(loadout).Should().Equal(before, "the item and its stored entry are not rewritten when nothing about them changed");
    }

    [Fact]
    public async Task AnEntryWithAnEmptyChecksumEqualsWhatWasStored()
    {
        var loadout = await CreateLoadout();
        await WritePak(RecordedPak, SyntheticPak.ForPublishedModule("MyMod", "My Mod", RecordUuid, SyntheticPak.Version(1, 2, 3), "4242"));
        await WriteModsettings(RecordedEntry("MyMod", "My Mod", "4242", md5: ""));
        await Apply(loadout);

        // A rewrite of an unchanged item adds no datoms, so only the comparison shows whether an empty stored
        // value reads as a change, which would open a reconcile transaction on every Apply.
        var entry = new ModsettingsDocument.ModuleEntry("MyMod", "My Mod", "4242", RecordUuid, SyntheticPak.Version(1, 2, 3).ToString(), "");
        var content = new GameInstalledContent("Test", RecordedPak, "My Mod", null, GameInstalledConfidence.GameRecord, false, [], entry);
        var probe = new SynchronizerProbe(ServiceProvider, Game);

        probe.ExtrasDiffer(Connection.Db, Item(Refresh(loadout), RecordedPak).Id, content).Should().BeFalse();
    }

    private sealed class SynchronizerProbe(IServiceProvider provider, IGame game)
        : BaldursGate3Synchronizer(provider, game, new Bg3PakModules(provider))
    {
        public bool ExtrasDiffer(IDb db, EntityId itemId, GameInstalledContent content) => GameInstalledExtrasDiffer(db, itemId, content);
    }

    [Fact]
    public async Task InGamePaksAreNeitherExternalChangesNorBackedUp()
    {
        var loadout = await Fixture();

        await Apply(loadout);

        var overrides = OverridesPaths(Refresh(loadout));
        overrides.Should().NotContain(Pak(RecordedPak).Path.ToString());
        overrides.Should().NotContain(Pak(PatternPak).Path.ToString());
        (await FileStore.HaveFile(await DiskHash(RecordedPak))).Should().BeFalse();
        (await FileStore.HaveFile(await DiskHash(PatternPak))).Should().BeFalse();
    }

    [Fact]
    public async Task AnInGamePakIsAGameFileTheSyncLeavesAlone()
    {
        var loadout = await Fixture();
        await Apply(loadout);

        var tree = await Synchronizer.BuildSyncTree(Refresh(loadout));
        Synchronizer.ProcessSyncTree(tree);

        foreach (var pak in new[] { RecordedPak, PatternPak })
        {
            tree[Pak(pak)].SourceItemType.Should().Be(LoadoutSourceItemType.Game);
            tree[Pak(pak)].Actions.Should().Be(Actions.DoNothing);
        }
    }

    [Fact]
    public async Task AHandDroppedPakWithoutAHandleStaysAnExternalChange()
    {
        var loadout = await Fixture();

        await Apply(loadout);

        Items(loadout).Select(i => i.SourceId).Should().NotContain(HandDroppedPak);
        OverridesPaths(Refresh(loadout)).Should().Contain(Pak(HandDroppedPak).Path.ToString());
    }

    [Fact]
    public async Task CleanFolderDeletesTheHandDroppedPakAndKeepsTheInGamePaks()
    {
        var loadout = await Fixture();
        var recorded = await DiskHash(RecordedPak);
        var pattern = await DiskHash(PatternPak);
        await Apply(loadout);

        await LoadoutManager.CleanGameFolder(Refresh(loadout));

        OnDisk(Pak(HandDroppedPak)).FileExists.Should().BeFalse("the hand-dropped pak was an external change");
        (await DiskHash(RecordedPak)).Should().Be(recorded);
        (await DiskHash(PatternPak)).Should().Be(pattern);
        Items(loadout).Select(i => i.SourceId).Should().BeEquivalentTo([RecordedPak, PatternPak]);
    }

    [Fact]
    public async Task ApplyAndResetLeaveTheInGamePaksOnDisk()
    {
        var loadout = await Fixture();
        var recorded = await DiskHash(RecordedPak);
        var pattern = await DiskHash(PatternPak);

        await Apply(loadout);
        (await DiskHash(RecordedPak)).Should().Be(recorded);
        (await DiskHash(PatternPak)).Should().Be(pattern);

        await Synchronizer.ResetToOriginalGameState(GameInstallation, GameInstallation.LocatorResult.LocatorIds.ToArray());

        (await DiskHash(RecordedPak)).Should().Be(recorded);
        (await DiskHash(PatternPak)).Should().Be(pattern);
    }

    [Fact]
    public async Task APakWhoseEntryTheGameDroppedKeepsItsConfidenceAndEntry()
    {
        var loadout = await Fixture();
        await Apply(loadout);

        // The game drops the entry of a pak the group switched off.
        await WriteModsettings();
        await Apply(loadout);

        var item = Item(loadout, RecordedPak);
        item.Confidence.Should().Be("GameRecord");
        item.TryGetAsBg3ModuleEntry(out var entry).Should().BeTrue("the stored entry is what brings the pak back");
        entry.Md5.Value.Should().Be(RecordMd5);
    }

    private async Task<List<Diagnostic>> Dependencies(Loadout.ReadOnly loadout)
    {
        var emitter = Game.DiagnosticEmitters.OfType<DependencyDiagnosticEmitter>().Single();
        return await emitter.Diagnose(Refresh(loadout), CancellationToken.None).ToListAsync();
    }

    private string Summary(Diagnostic diagnostic) => diagnostic.FormatSummary(ServiceProvider.GetRequiredService<IDiagnosticWriter>());

    private static readonly SyntheticPak.Dependency MissingDependency = new("Nowhere", MissingUuid, SyntheticPak.Version(1));

    [Fact]
    public async Task AnInGamePakWithAMissingDependencySaysTheGameInstalledIt()
    {
        var loadout = await CreateLoadout();
        await WritePak(PatternPak, SyntheticPak.Build(("Mods/OtherMod/meta.lsx",
            SyntheticPak.MetaLsx("OtherMod", "Other Mod", RecordUuid, SyntheticPak.Version(2), "9000", MissingDependency))));
        await Apply(loadout);

        var warning = (await Dependencies(loadout)).Should().ContainSingle().Subject;

        warning.Id.Should().Be(new DiagnosticId("NexusMods.Games.Larian.BaldursGate3", number: 9));
        Summary(warning).Should().Be("The mod Other Mod (installed by the game) is missing the required dependency 'Nowhere' v1.0.0+.");
    }

    [Fact]
    public async Task AnInGamePakWithAnOutdatedDependencySaysTheGameInstalledIt()
    {
        var loadout = await CreateLoadout();
        await WritePak(PatternPak, SyntheticPak.Build(("Mods/OtherMod/meta.lsx",
            SyntheticPak.MetaLsx("OtherMod", "Other Mod", RecordUuid, SyntheticPak.Version(2), "9000",
                new SyntheticPak.Dependency("Library", MissingUuid, SyntheticPak.Version(2))))));
        await WritePak("library-ab12.pak", SyntheticPak.ForPublishedModule("Library", "Library", MissingUuid, SyntheticPak.Version(1), "9001"));
        await Apply(loadout);

        var warning = (await Dependencies(loadout)).Should().ContainSingle().Subject;

        warning.Id.Should().Be(new DiagnosticId("NexusMods.Games.Larian.BaldursGate3", number: 10));
        Summary(warning).Should().Be("The mod Other Mod (installed by the game) requires at least version 2.0.0+ of 'Library' but only v1.0.0 is installed.");
    }

    [Fact]
    public async Task AnOverridesPakWithAMissingDependencyKeepsTheOverridesWording()
    {
        var loadout = await CreateLoadout();
        // No handle, so the pak is the user's and lands in the overrides group.
        await WritePak(HandDroppedPak, SyntheticPak.ForModule("Plain", "Plain", RecordUuid, SyntheticPak.Version(1), MissingDependency));
        await Apply(loadout);

        var warning = (await Dependencies(loadout)).Should().ContainSingle().Subject;

        warning.Id.Should().Be(new DiagnosticId("NexusMods.Games.Larian.BaldursGate3", number: 7));
        Summary(warning).Should().Contain("(in Overrides)");
    }

    [Fact]
    public async Task ADisabledGameInstalledGroupIsLeftOutOfTheDependencyCheck()
    {
        var loadout = await CreateLoadout();
        await WritePak(PatternPak, SyntheticPak.Build(("Mods/OtherMod/meta.lsx",
            SyntheticPak.MetaLsx("OtherMod", "Other Mod", RecordUuid, SyntheticPak.Version(2), "9000", MissingDependency))));
        await Apply(loadout);
        var group = GameInstalledGroup.FindByInstalledFor(Connection.Db, loadout.Id).Single();
        await DisableItem(group.Id);

        (await Dependencies(loadout)).Should().BeEmpty();
    }
}
