using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Games.TestFramework.Helpers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.Sdk;
using SilverModManager.Sdk.ForeignDeployments;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// A game folder that holds files Vortex deployed becomes loadout mods when the game starts being managed.
/// </summary>
public class ForeignDeploymentAdoptionTests(ITestOutputHelper helper) : AStubbedGameTest<ForeignDeploymentAdoptionTests>(helper)
{
    private const string Combat = "Better Combat-1234-1-0-2-1700000000";
    private const string Skin = "Fancy Skin-77-3-1700000001";

    private bool _withVortex = true;
    private bool _withPakType;
    private const string Pak = "Apparition-1162-1-01-1679514562";

    protected override IServiceCollection AddServices(IServiceCollection services)
        => base.AddServices(services).ReplaceDesktopNotifier();

    private RecordingDesktopNotifier Notifier => (RecordingDesktopNotifier)ServiceProvider.GetRequiredService<IDesktopNotifier>();

    private static GamePath Game(string path) => new(LocationId.Game, path);

    private AbsolutePath OnDisk(string path) => GameInstallation.Locations.ToAbsolutePath(Game(path));

    private async Task Write(string path, string content)
    {
        var file = OnDisk(path);
        file.Parent.CreateDirectory();
        await file.WriteAllTextAsync(content);
    }

    private async Task WriteManifest(string name, params (string RelPath, string Source)[] files)
    {
        var json = JsonSerializer.Serialize(new
        {
            instance = "default",
            version = 1,
            deploymentMethod = "hardlink_activator",
            gameId = "stubbedgame",
            deploymentTime = 1700000000,
            stagingPath = @"Z:\staging",
            targetPath = @"Z:\games\StubbedGame",
            files = files.Select(f => new { relPath = f.RelPath, source = f.Source, target = "", time = 1700000000 }).ToArray(),
        });
        await Write(name, json);
    }

    protected override async Task GenerateGameFiles()
    {
        if (!_withVortex) return;

        await Write("Data/combat.pak", "combat");
        await Write("Data/combat_extra.pak", "combat extra");
        await Write("Merged/skin.dll", "skin");
        await Write("Data/__folder_managed_by_vortex", "marker");
        await Write("Data/player_own.txt", "mine");
        await WriteManifest("vortex.deployment.json", (@"Data\combat.pak", Combat), (@"Data\combat_extra.pak", Combat), (@"Data\gone.pak", Combat));
        await WriteManifest("vortex.deployment.dinput.json", (@"Merged\skin.dll", Skin));

        if (!_withPakType) return;

        // A mod type that deploys below the game folder keeps its record there, with paths relative to that folder.
        await Write($"Mods/~pak/{Pak}/a.pak", "pak a");
        await Write($"Mods/~pak/{Pak}/b.pak", "pak b");
        await Write("Mods/~pak/vortex.deployment.stub-PAK-modtype.json", JsonSerializer.Serialize(new
        {
            instance = "default",
            version = 1,
            targetPath = @"Z:\games\StubbedGame\Mods\~pak",
            files = new[] { $@"{Pak}\a.pak", $@"{Pak}\b.pak" }.Select(rel => new { relPath = rel, source = Pak, target = "", time = 1700000000 }).ToArray(),
        }));
    }

    private static IEnumerable<ForeignDeploymentGroup.ReadOnly> Groups(IDb db, Loadout.ReadOnly loadout)
        => ForeignDeploymentGroup.All(db).Where(group => group.AsLoadoutItemGroup().AsLoadoutItem().LoadoutId == loadout.LoadoutId);

    private static string[] FilesOf(ForeignDeploymentGroup.ReadOnly group)
        => group.AsLoadoutItemGroup().Children.OfTypeLoadoutItemWithTargetPath().Select(file => ((GamePath)file.TargetPath).Path.ToString()).Order(StringComparer.Ordinal).ToArray();

    private static string[] OverridesPaths(Loadout.ReadOnly loadout)
        => LoadoutOverridesGroup.FindByOverridesFor(loadout.Db, loadout.Id)
            .SelectMany(group => group.AsLoadoutItemGroup().Children.OfTypeLoadoutItemWithTargetPath())
            .Select(file => ((GamePath)file.TargetPath).Path.ToString())
            .Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public async Task CreatingALoadoutAdoptsEachVortexSourceAsAGroup()
    {
        var loadout = await CreateLoadout();

        var groups = Groups(Connection.Db, loadout).OrderBy(group => group.Source).ToArray();
        groups.Select(group => group.AsLoadoutItemGroup().AsLoadoutItem().Name).Should().Equal("Better Combat", "Fancy Skin");
        groups.Select(group => group.Source).Should().Equal(Combat, Skin);
        groups.Select(group => group.Manager).Should().AllBe("Vortex");
        FilesOf(groups[0]).Should().Equal("Data/combat.pak", "Data/combat_extra.pak");
        FilesOf(groups[1]).Should().Equal("Merged/skin.dll");
    }

    [Fact]
    public async Task AModTypeManifestBelowTheGameFolderAdoptsItsModsWithPathsRelativeToItsFolder()
    {
        _withPakType = true;

        var loadout = await CreateLoadout();

        var pak = Groups(Connection.Db, loadout).Single(group => group.Source == Pak);
        pak.AsLoadoutItemGroup().AsLoadoutItem().Name.Should().Be("Apparition");
        FilesOf(pak).Should().Equal($"Mods/~pak/{Pak}/a.pak", $"Mods/~pak/{Pak}/b.pak");
        OverridesPaths(loadout).Should().NotContain(path => path.StartsWith("Mods/"), "the files and the record were not left as unknown files");
        OnDisk("Mods/~pak/vortex.deployment.stub-PAK-modtype.json").FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task AdoptedGroupsAreMyModsChildren()
    {
        _withPakType = true;

        var loadout = await CreateLoadout();

        var myMods = LoadoutItemGroupId.From(DefaultCollection.Of(Connection.Db, loadout.LoadoutId)!.Value.Value);
        var groups = Groups(Connection.Db, loadout).ToArray();
        groups.Should().HaveCount(3);
        groups.Select(group => group.AsLoadoutItemGroup().AsLoadoutItem().ParentId).Should().AllBeEquivalentTo(myMods);
    }

    [Fact]
    public async Task AGroupKeepsTheNexusModIdAndVersionOfItsSource()
    {
        var loadout = await CreateLoadout();

        var combat = Groups(Connection.Db, loadout).Single(group => group.Source == Combat);
        ForeignDeploymentGroup.NexusModId.TryGetValue(combat, out var combatId).Should().BeTrue();
        combatId.Should().Be(ModId.From(1234));
        ForeignDeploymentGroup.Version.TryGetValue(combat, out var combatVersion).Should().BeTrue();
        combatVersion.Should().Be("1.0.2");
        var skin = Groups(Connection.Db, loadout).Single(group => group.Source == Skin);
        ForeignDeploymentGroup.NexusModId.TryGetValue(skin, out var skinId).Should().BeTrue();
        skinId.Should().Be(ModId.From(77));
        ForeignDeploymentGroup.Version.TryGetValue(skin, out var skinVersion).Should().BeTrue();
        skinVersion.Should().Be("3");
    }

    [Fact]
    public async Task AdoptedFilesAreNotLeftInTheExternalChanges()
    {
        var loadout = await CreateLoadout();

        OverridesPaths(loadout).Should().NotContain(["Data/combat.pak", "Data/combat_extra.pak", "Merged/skin.dll"]);
    }

    [Fact]
    public async Task TheManifestsAndMarkersStayOnDiskAndAreNeverAModFile()
    {
        var loadout = await CreateLoadout();

        OnDisk("vortex.deployment.json").FileExists.Should().BeTrue();
        OnDisk("vortex.deployment.dinput.json").FileExists.Should().BeTrue();
        OnDisk("Data/__folder_managed_by_vortex").FileExists.Should().BeTrue();
        var rows = LoadoutItem.FindByLoadout(Connection.Db, loadout).OfTypeLoadoutItemWithTargetPath()
            .Select(file => ((GamePath)file.TargetPath).Path.ToString()).ToArray();
        rows.Should().NotContain(path => path.Contains("vortex.deployment") || path.Contains("__folder_managed_by_vortex"));
    }

    [Fact]
    public async Task ASourceWithNothingOnDiskAdoptsNothingAndTheMissingFileIsSkipped()
    {
        var loadout = await CreateLoadout();

        FilesOf(Groups(Connection.Db, loadout).Single(group => group.Source == Combat)).Should().NotContain("Data/gone.pak");
        OnDisk("Data/gone.pak").FileExists.Should().BeFalse();
    }

    [Fact]
    public async Task TheNoticeNamesTheMods()
    {
        await CreateLoadout();

        var notice = Notifier.Notifications.Should().ContainSingle().Subject;
        notice.Summary.Should().Be("2 mods were adopted from Vortex");
        notice.Body.Should().Contain("Better Combat (2 files)").And.Contain("Fancy Skin (1 file)");
        notice.Body.Should().Contain("1 file the manager lists was not on disk and was skipped.");
    }

    [Fact]
    public async Task AFileVortexBackedUpTheOriginalOfIsNotAdoptedAndTheNoticeSaysSo()
    {
        await Write("Data/combat.pak.vortex_backup", "the game's own");
        var loadout = await CreateLoadout();

        var combat = Groups(Connection.Db, loadout).Single(group => group.Source == Combat);
        FilesOf(combat).Should().Equal("Data/combat_extra.pak");
        var notice = Notifier.Notifications.Should().ContainSingle().Subject;
        notice.Body.Should().Contain("1 file was left alone because Vortex kept the game's original beside it");
        OnDisk("Data/combat.pak").FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task TheBaselineIsBuiltFromTheFilesAlreadyDetectedNotDetectedAgain()
    {
        var hashes = (StubbedFileHasherService)ServiceProvider.GetRequiredService<IFileHashesService>();

        await CreateLoadout();

        hashes.ForeignFilesPassed.Should().NotBeEmpty().And.OnlyContain(set => set != null);
        hashes.ForeignFilesPassed.Select(set => set!.Select(path => path.Path.ToString()).Order(StringComparer.Ordinal))
            .Should().AllSatisfy(set => set.Should().Equal("Data/combat.pak", "Data/combat_extra.pak", "Merged/skin.dll"));
    }

    [Fact]
    public async Task SwitchingAnAdoptedGroupOffRemovesItsFilesAndSwitchingItOnRestoresThem()
    {
        var loadout = await CreateLoadout();
        var combat = Groups(Connection.Db, loadout).Single(group => group.Source == Combat);

        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(combat.Id, LoadoutItem.Disabled, Null.Instance);
            await tx.Commit();
        }
        loadout = await Synchronizer.Synchronize(Refresh(loadout));

        OnDisk("Data/combat.pak").FileExists.Should().BeFalse();
        OnDisk("Data/combat_extra.pak").FileExists.Should().BeFalse();
        OnDisk("Merged/skin.dll").FileExists.Should().BeTrue("another mod is still on");
        OnDisk("vortex.deployment.json").FileExists.Should().BeTrue("the manifest is Vortex's");
        OnDisk("Data/__folder_managed_by_vortex").FileExists.Should().BeTrue();
        OnDisk("Data/player_own.txt").FileExists.Should().BeTrue();

        using (var tx = Connection.BeginTransaction())
        {
            tx.Retract(combat.Id, LoadoutItem.Disabled, Null.Instance);
            await tx.Commit();
        }
        await Synchronizer.Synchronize(Refresh(loadout));

        (await OnDisk("Data/combat.pak").ReadAllTextAsync()).Should().Be("combat");
        (await OnDisk("Data/combat_extra.pak").ReadAllTextAsync()).Should().Be("combat extra");
    }

    [Fact]
    public async Task AFileTheManifestListsStaysAdoptedWithTheBytesThatAreOnDisk()
    {
        await Write("Data/combat.pak", "edited after Vortex deployed it");
        var loadout = await CreateLoadout();

        var combat = Groups(Connection.Db, loadout).Single(group => group.Source == Combat);
        FilesOf(combat).Should().Contain("Data/combat.pak");
        OnDisk("Data/combat.pak").FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task ASecondLoadoutAdoptsNothingMore()
    {
        var first = await CreateLoadout();

        var second = await LoadoutManager.CreateLoadout(GameInstallation, "Second");

        Groups(Connection.Db, second).Should().BeEmpty();
        Groups(Connection.Db, first).Should().HaveCount(2);
        Notifier.Notifications.Should().ContainSingle();
    }

    [Fact]
    public async Task ARecordThatLeavesTheGameFolderIsRefusedBeforeAnythingIsCreated()
    {
        _withVortex = false;
        await WriteManifest("vortex.deployment.json", (@"..\..\escape.dll", Combat));

        var act = async () => await LoadoutManager.CreateLoadout(GameInstallation, "Refused");

        await act.Should().ThrowAsync<ForeignDeploymentRefusedException>().WithMessage("*outside the game folder*");
        Loadout.All(Connection.Db).Should().BeEmpty();
        Notifier.Notifications.Should().BeEmpty();
    }

    [Fact]
    public async Task AFolderWithoutManifestsAdoptsNothing()
    {
        _withVortex = false;
        await Write("Data/plain.pak", "plain");

        var loadout = await CreateLoadout();

        Groups(Connection.Db, loadout).Should().BeEmpty();
        Notifier.Notifications.Should().BeEmpty();
    }

    private async Task SeedSnapshotBaseline(params (string Path, string Content)[] vanilla)
    {
        var hashes = (StubbedFileHasherService)ServiceProvider.GetRequiredService<IFileHashesService>();
        await LocalBaselineRows.AddLocalBaseline(hashes.Local.Connection, GameInstallation.LocatorResult.Store,
            GameInstallation.Locations[LocationId.Game].Path.ToString(), LocalBaselineKind.Snapshot, vanilla);
    }

    [Fact]
    public async Task ASnapshotBaselineThatListsAVortexFileAsVanillaIsBuiltAgain()
    {
        await SeedSnapshotBaseline(("Data/combat.pak", "combat"), ("game.exe", "exe"));
        var hashes = (StubbedFileHasherService)ServiceProvider.GetRequiredService<IFileHashesService>();
        hashes.BaselineRequests.Clear();

        await CreateLoadout();

        hashes.RemovedBaselines.Should().ContainSingle();
        hashes.BaselineRequests.Count(request => request.LoadoutCount == 0).Should().Be(2, "the baseline is asked for again, before the loadout exists, after the old one is removed");
    }

    [Fact]
    public async Task ASnapshotBaselineWithoutTheVortexFilesIsKept()
    {
        await SeedSnapshotBaseline(("game.exe", "exe"));
        var hashes = (StubbedFileHasherService)ServiceProvider.GetRequiredService<IFileHashesService>();

        await CreateLoadout();

        hashes.RemovedBaselines.Should().BeEmpty();
    }
}
