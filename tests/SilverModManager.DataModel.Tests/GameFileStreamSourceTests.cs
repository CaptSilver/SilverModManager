using System.Text;
using FluentAssertions;
using NexusMods.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.IO;

namespace NexusMods.DataModel.Tests;

public class GameFileStreamSourceTests(ITestOutputHelper helper) : AStubbedGameIsolatedTest<GameFileStreamSourceTests>(helper)
{
    private const string Content = "a game file the diagnostics read";
    private static readonly GamePath FilePath = new(LocationId.Game, "Data/plugin.esm");

    protected override async Task GenerateGameFiles()
    {
        var file = GameInstallation.Locations.ToAbsolutePath(FilePath);
        file.Parent.CreateDirectory();
        await file.WriteAllTextAsync(Content);
    }

    [Fact]
    public async Task ResolvesAnIndexedGameFileByItsHash()
    {
        var loadout = await CreateLoadout();
        await Synchronizer.Synchronize(loadout);

        var source = new GameFileStreamSource(Connection, ServiceProvider);
        var hash = Encoding.UTF8.GetBytes(Content).xxHash3();

        source.Resolve(hash).Should().Be(GameInstallation.Locations.ToAbsolutePath(FilePath));

        await using var stream = await source.OpenAsync(hash);
        stream.Should().NotBeNull();
        using var reader = new StreamReader(stream!);
        (await reader.ReadToEndAsync()).Should().Be(Content);
    }

    [Fact]
    public async Task ResolvesAGameFileBeforeAnyLoadoutWasSynchronized()
    {
        // A loadout that was never applied (or whose game was reset) has no last synced loadout, but its
        // diagnostics still read game files.
        await CreateLoadout();
        await Synchronizer.ReindexState(GameInstallation);

        var source = new GameFileStreamSource(Connection, ServiceProvider);
        var hash = Encoding.UTF8.GetBytes(Content).xxHash3();

        source.Resolve(hash).Should().Be(GameInstallation.Locations.ToAbsolutePath(FilePath));
    }

    [Fact]
    public async Task ResolvesAGameFileOfAnInstallationThatHasNeverBeenSynchronized()
    {
        // The first synchronize of a game writes its intrinsic files, and Plugins.txt reads the game's plugins
        // for it, before anything records a last synced loadout.
        await GenerateGameFiles();
        await LoadoutManager.ManageInstallation(GameInstallation);
        var metadata = GameRegistry.ForceGetMetadata(GameInstallation);
        metadata.Contains(GameInstallMetadata.LastSyncedLoadout).Should().BeFalse();
        await Synchronizer.ReindexState(GameInstallation);

        var source = new GameFileStreamSource(Connection, ServiceProvider);

        source.Resolve(Encoding.UTF8.GetBytes(Content).xxHash3()).Should().Be(GameInstallation.Locations.ToAbsolutePath(FilePath));
    }

    [Fact]
    public async Task DoesNotResolveTheRecordedHashOfAFileTheGameStoreHasSinceReplaced()
    {
        // The loadout's game layer names the build it was created for. When the store later patches a file,
        // the bytes that layer lists are gone from the disk, and only the new bytes can be read.
        var loadout = await CreateLoadout();
        await Synchronizer.Synchronize(loadout);

        var file = GameInstallation.Locations.ToAbsolutePath(FilePath);
        const string patched = "the same file after the store patched it";
        await file.WriteAllTextAsync(patched);
        await Synchronizer.ReindexState(GameInstallation);

        var source = new GameFileStreamSource(Connection, ServiceProvider);
        source.Resolve(Encoding.UTF8.GetBytes(Content).xxHash3()).Should().BeNull();
        source.Resolve(Encoding.UTF8.GetBytes(patched).xxHash3()).Should().Be(file);
    }
}
