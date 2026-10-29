using System.Text;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Games;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.Witcher.Witcher3;
using SilverModManager.Games.Witcher.Witcher3.Models;
using SilverModManager.Games.Witcher.Witcher3.SortOrder;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.Witcher.Tests.Witcher3;

public class Witcher3SynchronizerTests(ITestOutputHelper helper)
    : AIsolatedGameTest<Witcher3SynchronizerTests, Witcher3Game>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddWitcherGames()
        .AddUniversalGameLocator<Witcher3Game>(new Version("4.0"), locationsFileSystem: IsolatedKnownFolders.Create);

    [Fact]
    public async Task AnApplyBuildsTheLoadOrder_SoAReplacedModsSettingsGetsItsNotice()
    {
        var loadout = await CreateLoadout();
        await Synchronizer.Synchronize(Refresh(loadout));

        // The Load Order of a loadout nobody has reconciled yet does not exist; Vortex wrote the file since the last apply.
        var variety = Game.SortOrderManager.GetSortOrderVarieties().OfType<WitcherModSortOrderVariety>().Single();
        await variety.DeleteSortOrder(variety.GetSortOrderIdFor(loadout.LoadoutId, Connection.Db).Value);
        variety.GetSortOrderIdFor(loadout.LoadoutId, Connection.Db).HasValue.Should().BeFalse();
        await GameInstallation.Locations.ToAbsolutePath(WitcherConstants.W3ModsSettings)
            .WriteAllBytesAsync(new UTF8Encoding(false).GetBytes("[mod0000_MergedFiles]\nPriority=1\n[modFromVortex]\nPriority=2\nVK=1\n"));

        await Synchronizer.Synchronize(Refresh(loadout));

        WitcherModsSettingsNotice.All(Connection.Db).Should().ContainSingle();
    }

    /// <summary>What the game's synchronizer did before targets were repaired: same files, no pre-step.</summary>
    private sealed class UnrepairedSynchronizer(IServiceProvider provider, IGame game, ALoadoutSynchronizer real) : WitcherSynchronizer(provider, game)
    {
        public override Dictionary<GamePath, IIntrinsicFile> IntrinsicFiles(Loadout.ReadOnly loadout) => real.IntrinsicFiles(loadout);
    }

    private async Task AddModFile(Loadout.ReadOnly loadout, string path, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = bytes.xxHash3();
        if (!await FileStore.HaveFile(hash))
            await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(path, new MemoryStream(bytes)), hash, Size.FromLong(bytes.Length))]);

        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, "mod " + path);
        AddFile(tx, loadout.LoadoutId, group, new GamePath(LocationId.Game, path), content);
        await tx.Commit();
    }

    private string[] GameFolderNames() =>
        GameInstallation.Locations[LocationId.Game].Path.EnumerateDirectories(recursive: false).Select(d => d.FileName).ToArray();

    [Fact]
    public async Task AnApplyMovesAFileDeployedUnderALowercaseModsFolderIntoMods_AndRemovesTheEmptiedFolder()
    {
        var loadout = await CreateLoadout();
        await AddModFile(loadout, "mods/modX/content/a.ws", "class A {}");

        // What an earlier build did: the file is deployed under the archive's spelling.
        await new UnrepairedSynchronizer(ServiceProvider, Game, (ALoadoutSynchronizer)Synchronizer).Synchronize(Refresh(loadout));
        GameInstallation.Locations[LocationId.Game].Path.Combine("mods/modX/content/a.ws").FileExists.Should().BeTrue();
        GameFolderNames().Should().Contain("mods");

        await Synchronizer.Synchronize(Refresh(loadout));

        GameInstallation.Locations[LocationId.Game].Path.Combine("Mods/modX/content/a.ws").FileExists.Should().BeTrue();
        GameFolderNames().Should().Contain("Mods").And.NotContain("mods");
    }

    [Fact]
    public async Task AnApplyRewritesTargetsInAnyCaseOfModsDlcAndBin_ToTheGamesSpelling()
    {
        var loadout = await CreateLoadout();
        await AddModFile(loadout, "MODS/modX/content/a.ws", "a");
        await AddModFile(loadout, "Dlc/dlcY/content/b.bundle", "b");
        await AddModFile(loadout, "BIN/x64/c.dll", "c");
        await AddModFile(loadout, "Mods/modZ/content/d.ws", "d");

        await Synchronizer.Synchronize(Refresh(loadout));

        var targets = LoadoutItem.FindByLoadout(Connection.Db, loadout.LoadoutId).OfTypeLoadoutItemWithTargetPath().OfTypeLoadoutFile()
            .Select(f => ((GamePath)f.AsLoadoutItemWithTargetPath().TargetPath).ToString()).Order(StringComparer.Ordinal).ToArray();
        targets.Should().Equal("{Game}/Mods/modX/content/a.ws", "{Game}/Mods/modZ/content/d.ws", "{Game}/bin/x64/c.dll", "{Game}/dlc/dlcY/content/b.bundle");
    }

    [Fact]
    public async Task AnApplyMergesLowercaseModsBesideMods_AndKeepsEveryModsFile()
    {
        var loadout = await CreateLoadout();
        await AddModFile(loadout, "mods/modMenu/content/a.ws", "menu");
        await AddModFile(loadout, "Mods/modSharedutils/content/b.ws", "base");
        await AddModFile(loadout, "mods/modSharedutils/content/c.ws", "more");
        await new UnrepairedSynchronizer(ServiceProvider, Game, (ALoadoutSynchronizer)Synchronizer).Synchronize(Refresh(loadout));
        GameFolderNames().Should().Contain("mods").And.Contain("Mods");

        await Synchronizer.Synchronize(Refresh(loadout));

        var game = GameInstallation.Locations[LocationId.Game].Path;
        game.Combine("Mods/modMenu/content/a.ws").FileExists.Should().BeTrue();
        game.Combine("Mods/modSharedutils/content/b.ws").FileExists.Should().BeTrue();
        game.Combine("Mods/modSharedutils/content/c.ws").FileExists.Should().BeTrue();
        GameFolderNames().Should().NotContain("mods");
    }

    [Fact]
    public async Task ARepairedFileCanStillBeRemovedByTheNextApply()
    {
        var loadout = await CreateLoadout();
        await AddModFile(loadout, "mods/modX/content/a.ws", "class A {}");
        await new UnrepairedSynchronizer(ServiceProvider, Game, (ALoadoutSynchronizer)Synchronizer).Synchronize(Refresh(loadout));
        await Synchronizer.Synchronize(Refresh(loadout));

        using (var tx = Connection.BeginTransaction())
        {
            foreach (var group in LoadoutItemGroup.All(Connection.Db).Where(g => g.AsLoadoutItem().Name.StartsWith("mod mods/", StringComparison.Ordinal)))
                tx.Add(group.Id, LoadoutItem.Disabled, Null.Instance);
            await tx.Commit();
        }
        await Synchronizer.Synchronize(Refresh(loadout));

        var game = GameInstallation.Locations[LocationId.Game].Path;
        game.Combine("Mods/modX/content/a.ws").FileExists.Should().BeFalse();
        GameFolderNames().Should().NotContain("mods");
    }
}
