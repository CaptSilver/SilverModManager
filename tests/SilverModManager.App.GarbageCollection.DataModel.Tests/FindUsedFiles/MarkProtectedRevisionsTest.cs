using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.GC;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.MnemonicDB.Abstractions.BuiltInEntities;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.IO;
using Xunit;

namespace SilverModManager.App.GarbageCollection.DataModel.Tests.FindUsedFiles;

/// <summary>
/// A restore point can only be reverted to if the bytes its loadout held are still in the file store, so a protected
/// revision keeps them out of a collection even after the live rows that named them are gone.
/// </summary>
public class MarkProtectedRevisionsTest(ITestOutputHelper testOutputHelper) : AGCStubbedGameTest<MarkProtectedRevisionsTest>(testOutputHelper)
{
    [Fact]
    public async Task AFileALoadoutHeldAtAProtectedRevisionIsStillReferencedAfterItsRowIsDeleted()
    {
        var (loadout, file, _) = await HoldAFileAndProtectIt();
        await DeleteFileRows(loadout);

        await CollectGarbage();

        (await FileStore.HaveFile(file)).Should().BeTrue("undoing to the protected revision needs the bytes");
    }

    [Fact]
    public async Task AnIniCopyALoadoutHeldAtAProtectedRevisionIsStillReferencedAfterItsRowIsDeleted()
    {
        var loadout = await CreateLoadout();
        var baseline = await Store("[Display]\r\niSize W=2560\r\n");
        var baked = await Store("[Display]\r\niSize W=1920\r\n");
        await AddIniState(loadout, baseline, baked);
        var restorePoint = await LoadoutSnapshot.WriteRestorePoint(Connection, loadout.LoadoutId);
        await Protect(loadout, restorePoint);
        await DeleteIniStates(loadout);

        await CollectGarbage();

        (await FileStore.HaveFile(FileIdentity.XxHash3(baseline))).Should().BeTrue("the baseline is part of the protected state");
        (await FileStore.HaveFile(FileIdentity.XxHash3(baked))).Should().BeTrue("so are the bytes last baked");
    }

    [Fact]
    public async Task TheSameFileIsNotReferencedOnceTheProtectionRowIsGone()
    {
        var (loadout, file, protection) = await HoldAFileAndProtectIt();
        await DeleteFileRows(loadout);
        using (var tx = Connection.BeginTransaction())
        {
            tx.Delete(protection, recursive: false);
            await tx.Commit();
        }

        await CollectGarbage();

        (await FileStore.HaveFile(file)).Should().BeFalse("nothing refers to it any more");
    }

    [Fact]
    public async Task AProtectedRevisionOfADeletedLoadoutIsIgnored()
    {
        var (loadout, file, _) = await HoldAFileAndProtectIt();
        await DeleteLoadoutAsync(loadout.LoadoutId);

        await CollectGarbage();

        (await FileStore.HaveFile(file)).Should().BeFalse("the loadout it belonged to is gone");
    }

    private async Task<(Loadout.ReadOnly Loadout, FileIdentity File, EntityId Protection)> HoldAFileAndProtectIt()
    {
        var loadout = await CreateLoadout();
        List<FileIdentity> identities;
        using (var tx = Connection.BeginTransaction())
        {
            (_, identities) = await AddModAsync(tx, [RelativePath.FromUnsanitizedInput("protected-file.txt")], loadout, "A mod");
            await tx.Commit();
        }

        var restorePoint = await LoadoutSnapshot.WriteRestorePoint(Connection, loadout.LoadoutId);
        var protection = await Protect(loadout, restorePoint);
        return (loadout, identities[0], protection);
    }

    private async Task<EntityId> Protect(Loadout.ReadOnly loadout, TxId restorePoint)
    {
        using var tx = Connection.BeginTransaction();
        var row = new ProtectedLoadoutRevision.New(tx)
        {
            LoadoutId = loadout.LoadoutId,
            RevisionId = TransactionId.From(EntityId.From(restorePoint.Value)),
            Reason = "test",
        };
        var result = await tx.Commit();
        return result.Remap(row).Id;
    }

    private async Task DeleteFileRows(Loadout.ReadOnly loadout)
    {
        using var tx = Connection.BeginTransaction();
        foreach (var file in LoadoutFile.All(Connection.Db).Where(f => f.AsLoadoutItemWithTargetPath().AsLoadoutItem().LoadoutId == loadout.LoadoutId))
            tx.Delete(file.Id, recursive: false);
        await tx.Commit();
    }

    private async Task AddIniState(Loadout.ReadOnly loadout, Hash baseline, Hash baked)
    {
        using var tx = Connection.BeginTransaction();
        _ = new LoadoutIniState.New(tx)
        {
            LoadoutId = loadout.LoadoutId,
            Path = new GamePath(LocationId.Preferences, "Game.ini").ToGamePathParentTuple(loadout.LoadoutId),
            Baseline = baseline,
            LastBakedHash = baked,
        };
        await tx.Commit();
    }

    private async Task DeleteIniStates(Loadout.ReadOnly loadout)
    {
        using var tx = Connection.BeginTransaction();
        foreach (var state in LoadoutIniState.All(Connection.Db).Where(s => s.LoadoutId == loadout.LoadoutId))
            tx.Delete(state.Id, recursive: false);
        await tx.Commit();
    }

    private async Task CollectGarbage() =>
        await ServiceProvider.GetRequiredService<IGarbageCollectorRunner>().RunAsync();

    private async Task<Hash> Store(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var hash = bytes.xxHash3();
        await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory("ini", new MemoryStream(bytes)), FileIdentity.XxHash3(hash), Size.FromLong(bytes.Length))]);
        return hash;
    }
}
