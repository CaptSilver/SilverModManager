using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// Source and project files no game loads are never written, ingested or reported. An older build did write them
/// (a mod's <c>src/</c> folder), so a copy the app put there itself is still removed when its mod goes.
/// </summary>
public class NeverLoadedFilesSyncTests(ITestOutputHelper helper) : AStubbedGameTest<NeverLoadedFilesSyncTests>(helper)
{
    private static readonly GamePath Source = new(LocationId.Game, "src/main.cpp");
    private static readonly byte[] SourceBytes = "src/main.cpp"u8.ToArray();

    // What the older build wrote differs from anything the mod stores, so a backup of it can only come from the delete.
    private static readonly byte[] WrittenBytes = "src/main.cpp as an older build wrote it"u8.ToArray();

    private static FileIdentity IdentityOf(byte[] bytes) => FileIdentity.XxHash3(bytes.xxHash3());

    private AbsolutePath SourceOnDisk => GameInstallation.Locations.ToAbsolutePath(Source);

    // Rows are added straight to the loadout: the install path no longer unpacks these files, and an older build's
    // loadout is exactly what is being modelled. The file store holds each file's own path as its bytes.
    private async Task<(Loadout.ReadOnly Loadout, EntityId Mod)> InstallMod(Loadout.ReadOnly loadout, params RelativePath[] paths)
    {
        using var tx = Connection.BeginTransaction();
        await AddModAsync(tx, paths, loadout.LoadoutId, "mod");
        await tx.Commit();
        loadout = Refresh(loadout);
        return (loadout, LoadoutItem.FindByLoadout(Connection.Db, loadout).Single(item => item.Name == "mod").Id);
    }

    /// <summary>
    /// What an older build left behind: the file on disk, a disk state entry for it, and that entry part of the
    /// state recorded by the last apply.
    /// </summary>
    private async Task RecordAsWrittenByAnOlderBuild(GamePath path, byte[] bytes, bool inLastAppliedState = true)
    {
        var onDisk = GameInstallation.Locations.ToAbsolutePath(path);
        onDisk.Parent.CreateDirectory();
        await onDisk.WriteAllBytesAsync(bytes);

        var metadata = GameRegistry.ForceGetMetadata(GameInstallation);
        using var tx = Connection.BeginTransaction();
        _ = new DiskStateEntry.New(tx, tx.TempId(DiskStateEntry.EntryPartition))
        {
            Path = path.ToGamePathParentTuple(metadata.Id),
            Identity = FileIdentity.XxHash3(bytes.xxHash3()),
            XxHash3 = bytes.xxHash3(),
            Size = Size.FromLong(bytes.Length),
            LastModified = DateTimeOffset.UtcNow,
            GameId = metadata.Id,
        };
        if (inLastAppliedState) tx.Add(metadata.Id, GameInstallMetadata.LastSyncedLoadoutTransaction, EntityId.From(tx.ThisTxId.Value));
        tx.Add(metadata.Id, GameInstallMetadata.LastScannedDiskStateTransaction, EntityId.From(tx.ThisTxId.Value));
        await tx.Commit();
    }

    private List<GamePath> IndexedPaths()
        => Synchronizer.GetDiskStateForGame(GameRegistry.ForceGetMetadata(GameInstallation)).Select(p => p.Path).ToList();

    private async Task RemoveMod(EntityId mod)
    {
        using var tx = Connection.BeginTransaction();
        tx.Delete(mod, recursive: true);
        await tx.Commit();
    }

    [Fact]
    public async Task ASourceFileTheAppWrote_IsDeletedWhenItsModIsRemoved()
    {
        var loadout = await CreateLoadout();
        var (installed, mod) = await InstallMod(loadout, "src/main.cpp", "game/ok.txt");
        loadout = await Synchronizer.Synchronize(installed);
        await RecordAsWrittenByAnOlderBuild(Source, WrittenBytes);
        (await FileStore.HaveFile(IdentityOf(WrittenBytes))).Should().BeFalse("nothing has backed it up yet");

        await RemoveMod(mod);
        await Synchronizer.Synchronize(Refresh(loadout));

        SourceOnDisk.FileExists.Should().BeFalse("the app put it there for a mod that is gone");
        (await FileStore.HaveFile(IdentityOf(WrittenBytes))).Should().BeTrue("the delete backs the file up first");
        IndexedPaths().Should().NotContain(Source, "the deletion is recorded, so it does not repeat");
        GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, "game/ok.txt")).FileExists.Should().BeFalse();
    }

    [Fact]
    public async Task ASourceFileTheAppWrote_IsDeletedEvenWhileItsModStaysInTheLoadout()
    {
        var loadout = await CreateLoadout();
        var (installed, _) = await InstallMod(loadout, "src/main.cpp");
        loadout = await Synchronizer.Synchronize(installed);
        await RecordAsWrittenByAnOlderBuild(Source, WrittenBytes);
        (await FileStore.HaveFile(IdentityOf(WrittenBytes))).Should().BeFalse("nothing has backed it up yet");

        await Synchronizer.Synchronize(Refresh(loadout));

        SourceOnDisk.FileExists.Should().BeFalse("the mod would not install it today");
        (await FileStore.HaveFile(IdentityOf(WrittenBytes))).Should().BeTrue("the delete backs the file up first");
        IndexedPaths().Should().NotContain(Source);
    }

    [Fact]
    public async Task ASourceFileTheAppWroteThatWasEditedSince_IsLeftAlone()
    {
        var loadout = await CreateLoadout();
        var (installed, mod) = await InstallMod(loadout, "src/main.cpp");
        loadout = await Synchronizer.Synchronize(installed);
        await RecordAsWrittenByAnOlderBuild(Source, SourceBytes);
        var edited = "int main() { return 1; }"u8.ToArray();
        await SourceOnDisk.WriteAllBytesAsync(edited);

        await RemoveMod(mod);
        loadout = await Synchronizer.Synchronize(Refresh(loadout));

        SourceOnDisk.FileExists.Should().BeTrue("the user changed it, so it is theirs now");
        (await SourceOnDisk.ReadAllBytesAsync()).Should().Equal(edited);
        LoadoutItem.FindByLoadout(loadout.Db, loadout).OfTypeLoadoutItemWithTargetPath()
            .Select(i => (GamePath)i.TargetPath).Should().NotContain(Source, "it is not ingested either");
    }

    [Fact]
    public async Task ASourceFileTheAppNeverWrote_IsLeftAloneAndNotIngested()
    {
        var loadout = await CreateLoadout();
        SourceOnDisk.Parent.CreateDirectory();
        await SourceOnDisk.WriteAllBytesAsync(SourceBytes);

        loadout = await Synchronizer.Synchronize(loadout);

        SourceOnDisk.FileExists.Should().BeTrue();
        LoadoutItem.FindByLoadout(loadout.Db, loadout).OfTypeLoadoutItemWithTargetPath()
            .Select(i => (GamePath)i.TargetPath).Should().NotContain(Source);
        IndexedPaths().Should().NotContain(Source);
        (await FileStore.HaveFile(FileIdentity.XxHash3(SourceBytes.xxHash3()))).Should().BeFalse("it is not backed up");
        (await Synchronizer.BuildSyncTree(loadout)).Should().NotContainKey(Source, "it is not an external change");
    }

    [Fact]
    public async Task ASourceFileTheAppNeverWrote_SurvivesRemovingAModThatShipsTheSamePath()
    {
        var loadout = await CreateLoadout();
        var (installed, mod) = await InstallMod(loadout, "src/main.cpp");
        loadout = await Synchronizer.Synchronize(installed);
        SourceOnDisk.Parent.CreateDirectory();
        await SourceOnDisk.WriteAllBytesAsync(SourceBytes);

        await RemoveMod(mod);
        await Synchronizer.Synchronize(Refresh(loadout));

        SourceOnDisk.FileExists.Should().BeTrue("the app never wrote this copy");
    }

    [Fact]
    public async Task ASourceFileInAMod_IsNotWritten()
    {
        var loadout = await CreateLoadout();
        var (installed, _) = await InstallMod(loadout, "src/main.cpp", "game/ok.txt");

        await Synchronizer.Synchronize(installed);

        SourceOnDisk.FileExists.Should().BeFalse();
        GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, "game/ok.txt")).FileExists.Should().BeTrue("guards against nothing having been written at all");
        IndexedPaths().Should().NotContain(Source);
    }

    [Fact]
    public async Task ASourceFileIndexedButNotInTheLastAppliedState_IsForgottenNotIngested()
    {
        var loadout = await CreateLoadout();
        await RecordAsWrittenByAnOlderBuild(Source, SourceBytes, inLastAppliedState: false);

        loadout = await Synchronizer.Synchronize(loadout);

        SourceOnDisk.FileExists.Should().BeTrue("nothing shows the app wrote it");
        LoadoutItem.FindByLoadout(loadout.Db, loadout).OfTypeLoadoutItemWithTargetPath()
            .Select(i => (GamePath)i.TargetPath).Should().NotContain(Source, "it is not an external change");
        IndexedPaths().Should().NotContain(Source, "it is no longer tracked");
    }

    [Fact]
    public async Task ASourceFileInAModAllowedOnProtectedPaths_IsNotWritten()
    {
        var loadout = await CreateLoadout();
        var (installed, mod) = await InstallMod(loadout, "src/main.cpp", "game/ok.txt");
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(mod, LoadoutItem.ProtectedPathsAllowed, Null.Instance);
            await tx.Commit();
        }

        await Synchronizer.Synchronize(Refresh(installed));

        SourceOnDisk.FileExists.Should().BeFalse("the flag is for saves, nothing makes a source file loadable");
        GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, "game/ok.txt")).FileExists.Should().BeTrue();
    }
}
