using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.GC;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using Xunit;

namespace SilverModManager.App.GarbageCollection.DataModel.Tests.FindUsedFiles;

/// <summary>
/// A loadout's copy of a game INI and the bytes last baked from it are kept in the file store and nowhere else, so
/// a collection has to leave them alone.
/// </summary>
public class MarkGameIniCopiesTest(ITestOutputHelper testOutputHelper) : AGCStubbedGameTest<MarkGameIniCopiesTest>(testOutputHelper)
{
    [Fact]
    public async Task TheBaselineAndTheLastBakeAreRooted()
    {
        var loadout = await CreateLoadout();
        var baseline = await Store("[Display]\r\niSize W=2560\r\n"u8.ToArray());
        var baked = await Store("[Display]\r\niSize W=1920\r\n"u8.ToArray());
        using (var tx = Connection.BeginTransaction())
        {
            _ = new LoadoutIniState.New(tx)
            {
                LoadoutId = loadout.LoadoutId,
                Path = new GamePath(LocationId.Preferences, "Game.ini").ToGamePathParentTuple(loadout.LoadoutId),
                Baseline = baseline,
                LastBakedHash = baked,
            };
            await tx.Commit();
        }

        var unreferenced = await Store("[Display]\r\niSize W=640\r\n"u8.ToArray());

        await ServiceProvider.GetRequiredService<IGarbageCollectorRunner>().RunAsync();

        (await FileStore.HaveFile(unreferenced)).Should().BeFalse("the collection ran and removed what nothing refers to");
        (await FileStore.HaveFile(baseline)).Should().BeTrue("the baseline is the player's own copy");
        (await FileStore.HaveFile(baked)).Should().BeTrue("an ingest diffs the live file against it");
    }

    private async Task<Hash> Store(byte[] bytes)
    {
        var hash = bytes.xxHash3();
        await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory("ini", new MemoryStream(bytes)), hash, Size.FromLong(bytes.Length))]);
        return hash;
    }
}
