using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// A save a mod deployed is the loadout's own until the game rewrites it. When the disk side is named by another kind
/// than the loadout's copy and no recorded relation joins them, the two cannot be told apart, and the file is treated
/// as changed: left alone, and not counted as deployed.
/// </summary>
public class ProtectedPathIdentityTests(ITestOutputHelper helper) : AStubbedGameTest<ProtectedPathIdentityTests>(helper)
{
    private static readonly GamePath ModSavePath = new(LocationId.Saves, "saves/mod.sav");
    private static readonly FileIdentity SomeSha1 = FileIdentity.Sha1(Sha1Value.FromHex(new string('B', 40)));

    private ALoadoutSynchronizer Sync => (ALoadoutSynchronizer)Synchronizer;

    private async Task<Loadout.ReadOnly> DeployAModSave()
    {
        var loadout = await CreateLoadout();
        await using var archiveFile = await CreateTestArchive(new Dictionary<RelativePath, byte[]> { { "saves/mod.sav", "a save shipped by a mod"u8.ToArray() } });
        var archive = await RegisterLocalArchive(archiveFile.Path);
        var result = await LoadoutManager.InstallItem(archive.AsLibraryFile().AsLibraryItem(), loadout, installer: new StubbedGameInstaller(ServiceProvider));
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(result.LoadoutItemGroup!.Value.Id, LoadoutItem.ProtectedPathsAllowed, Null.Instance);
            await tx.Commit();
        }
        return await Synchronizer.Synchronize(Refresh(loadout));
    }

    private List<PathPartPair> DiskState(Func<SyncNodePart, SyncNodePart> modSave)
        => Synchronizer.GetDiskStateForGame(GameRegistry.ForceGetMetadata(GameInstallation))
            .Select(p => p.Path == ModSavePath ? p with { Part = modSave(p.Part) } : p).ToList();

    private static SyncNodePart NamedBySha1(SyncNodePart part) => part with { Identity = SomeSha1 };

    [Fact]
    public async Task ASaveWithNoPreviousStateWhoseDiskPartIsOfAnotherKind_IsLeftOutOfTheTree()
    {
        var loadout = Refresh(await DeployAModSave());
        var unresolved = DiskState(NamedBySha1);
        unresolved.Single(p => p.Path == ModSavePath).Part.Identity.MatchSameKind(
            Sync.BuildSyncTree(DiskState(p => p), [], loadout)[ModSavePath].Loadout.Identity).Should().Be(IdentityMatch.Unknown);

        Sync.BuildSyncTree(unresolved, [], loadout).Should().NotContainKey(ModSavePath, "it cannot be shown to be the loadout's copy, so it is the player's");
    }

    [Fact]
    public async Task ASaveWithNoPreviousStateWhoseDiskPartIsTheLoadoutsCopy_StaysInTheTree()
    {
        var loadout = Refresh(await DeployAModSave());

        Sync.BuildSyncTree(DiskState(p => p), [], loadout).Should().ContainKey(ModSavePath);
    }

    [Fact]
    public async Task ASaveWhoseDiskPartIsOfAnotherKindThanTheLoadoutsCopy_IsNotCountedAsDeployed()
    {
        await DeployAModSave();
        var metadata = GameRegistry.ForceGetMetadata(GameInstallation);

        Sync.GetFilesDeployedByLastSyncedLoadout(metadata, DiskState(p => p)).Should().Contain(ModSavePath);
        Sync.GetFilesDeployedByLastSyncedLoadout(metadata, DiskState(NamedBySha1)).Should().NotContain(ModSavePath);
    }
}
