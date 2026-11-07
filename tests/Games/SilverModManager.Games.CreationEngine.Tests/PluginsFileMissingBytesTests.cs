using System.Collections.Immutable;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mutagen.Bethesda;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.Bethesda.LoadOrder;
using SilverModManager.Games.CreationEngine.Abstractions;
using SilverModManager.Games.CreationEngine.PluginLists;
using SilverModManager.Games.CreationEngine.SortOrder;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests;

/// <summary>
/// A plugin the loadout lists but whose bytes the file store does not hold (a game file patched after the loadout was
/// made). Rendering the plugin list has to cope the way the Apply does, because the Apply button compares a render
/// with what the last Apply wrote.
/// </summary>
public sealed class PluginsFileMissingBytesTests : IDisposable
{
    private static readonly GamePath DataFolder = new(LocationId.Game, "Data");
    private static readonly FileIdentity StoredIdentity = FileIdentity.XxHash3(Hash.From(1));

    private readonly string _root = Path.Combine(Path.GetTempPath(), "plugins-missing-bytes-" + Guid.NewGuid().ToString("N"));
    private readonly GameInstallation _installation;

    public PluginsFileMissingBytesTests()
    {
        var game = FileSystem.Shared.FromUnsanitizedFullPath(Path.Combine(_root, "game"));
        Directory.CreateDirectory(game.Combine("Data").ToString());
        var locations = GameLocations.Create(new Dictionary<LocationId, AbsolutePath> { [LocationId.Game] = game }.ToImmutableDictionary());
        _installation = new GameInstallation(new GameLocatorResult
        {
            Game = null!, Path = game, Store = GameStore.Steam, StoreIdentifier = "1", LocatorIds = [], Locator = null!,
        }, locations);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>A game whose file store holds none of the plugins' bytes.</summary>
    private static ICreationEngineGame GameWithEmptyStore()
    {
        var game = Substitute.For<ICreationEngineGame>();
        game.DataPath.Returns(DataFolder);
        game.PluginsFile.Returns(new GamePath(LocationId.AppData, "plugins.txt"));
        game.HeaderFormat.Returns(PluginFormat.Tes4);
        game.ParsePlugin(default, null).ReturnsForAnyArgs(ValueTask.FromResult<PluginHeader?>(null));
        game.ParsePluginFile(default, null).ReturnsForAnyArgs(call => ValueTask.FromResult(ReadHeader(call.ArgAt<AbsolutePath>(0), call.ArgAt<RelativePath?>(1))));
        game.IsMaster(default!, default!).ReturnsForAnyArgs(call =>
            call.ArgAt<PluginHeader>(0).FileName.EndsWith(".esm", StringComparison.OrdinalIgnoreCase));
        game.ImplicitlyActivePlugins(default!).ReturnsForAnyArgs(call => call.Arg<IEnumerable<string>>().Where(n => n == "SkyrimVR.esm").ToList());
        return game;
    }

    /// <summary>What the real game does for a file in the game folder, with the one format these tests write.</summary>
    private static PluginHeader? ReadHeader(AbsolutePath file, RelativePath? name)
    {
        if (!file.FileExists) return null;
        try
        {
            using var stream = file.Read();
            return PluginHeaderReader.Read(stream, PluginFormat.Tes4, name?.FileName.ToString() ?? "unknown.esm");
        }
        catch (InvalidPluginException)
        {
            return null;
        }
    }

    private static SyncNode GameFile(FileIdentity? onDisk = null) => new()
    {
        Loadout = new SyncNodePart { Identity = StoredIdentity, Size = NexusMods.Paths.Size.From(10), LastModifiedTicks = 0 },
        Disk = onDisk is { } identity ? new SyncNodePart { Identity = identity, Size = NexusMods.Paths.Size.From(10), LastModifiedTicks = 0 } : default,
        SourceItemType = LoadoutSourceItemType.Game,
    };

    private static Dictionary<GamePath, SyncNode> Tree(params (string Name, SyncNode Node)[] files) =>
        files.ToDictionary(f => new GamePath(LocationId.Game, "Data/" + f.Name), f => f.Node);

    private void PutOnDisk(string name, byte[] bytes) => File.WriteAllBytes(_installation.Locations.ToAbsolutePath(DataFolder / name).ToString(), bytes);

    private async Task<string[]> Lines(ICreationEngineGame game, Dictionary<GamePath, SyncNode> tree, string[] earlyLoaders, params PluginSortData[] rows)
    {
        var file = new PluginsFile(NullLogger<PluginsFile>.Instance, game, new AsteriskMechanism());
        using var stream = new MemoryStream();
        await file.Render(stream, _installation, earlyLoaders, rows, tree);
        return Encoding.UTF8.GetString(stream.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith('#'))
            .ToArray();
    }

    private static readonly string[] SkyrimVrMasters = ["Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm", "SkyrimVR.esm"];

    [Fact]
    public async Task TheAlwaysLoadedMastersWithNoStoredBytes_AreNotNeededToRenderTheList()
    {
        var game = GameWithEmptyStore();
        var tree = Tree([.. SkyrimVrMasters.Select(name => (name, GameFile())), ("Mod.esp", GameFile())]);
        // Mod.esp's bytes are on disk; the six masters have nothing anywhere.
        PutOnDisk("Mod.esp", BethesdaPluginBytes.Bytes(GameRelease.SkyrimVR, "Mod.esp"));
        tree[new GamePath(LocationId.Game, "Data/Mod.esp")] = GameFile(FileIdentity.XxHash3(Hash.From(5)));

        var lines = await Lines(game, tree, SkyrimVrMasters, new PluginSortData("Mod.esp", 0, true));

        lines.Should().Equal("*Mod.esp");
    }

    [Fact]
    public async Task ARegularPluginWithNoStoredBytes_IsReadFromItsCopyOnDisk()
    {
        var game = GameWithEmptyStore();
        // The disk copy of B.esp needs A.esp, so A.esp has to be listed first even though its row says second.
        var tree = Tree(("A.esp", GameFile(FileIdentity.XxHash3(Hash.From(5)))), ("B.esp", GameFile(FileIdentity.XxHash3(Hash.From(6)))));
        PutOnDisk("A.esp", BethesdaPluginBytes.Bytes(GameRelease.SkyrimSE, "A.esp"));
        PutOnDisk("B.esp", BethesdaPluginBytes.Bytes(GameRelease.SkyrimSE, "B.esp", "A.esp"));

        var lines = await Lines(game, tree, [], new PluginSortData("B.esp", 0, true), new PluginSortData("A.esp", 1, true));

        lines.Should().Equal("*A.esp", "*B.esp");
    }

    [Fact]
    public async Task ARegularPluginWithNoBytesAnywhere_RefusesTheListLikeTheApplyDoes()
    {
        var game = GameWithEmptyStore();
        var tree = Tree(("Gone.esp", GameFile()), ("Garbled.esp", GameFile(FileIdentity.XxHash3(Hash.From(7)))));
        PutOnDisk("Garbled.esp", "not a plugin"u8.ToArray());

        var act = () => Lines(game, tree, [], new PluginSortData("Gone.esp", 0, true));

        (await act.Should().ThrowAsync<UnreadablePluginsException>()).Which.PluginNames.Should().BeEquivalentTo("Gone.esp", "Garbled.esp");
    }

    [Fact]
    public async Task TheAlwaysLoadedMastersWithReadableDiskCopies_AreReadForTheirFlagsAndOrder()
    {
        var game = GameWithEmptyStore();
        var tree = Tree([.. SkyrimVrMasters.Select(name => (name, GameFile(FileIdentity.XxHash3(Hash.From(9))))), ("Mod.esp", GameFile(FileIdentity.XxHash3(Hash.From(5)))), ("Late.esm", GameFile(FileIdentity.XxHash3(Hash.From(6))))]);
        foreach (var name in SkyrimVrMasters) PutOnDisk(name, BethesdaPluginBytes.Bytes(GameRelease.SkyrimVR, name));
        PutOnDisk("Mod.esp", BethesdaPluginBytes.Bytes(GameRelease.SkyrimVR, "Mod.esp", "Dragonborn.esm"));
        PutOnDisk("Late.esm", BethesdaPluginBytes.Bytes(GameRelease.SkyrimVR, "Late.esm"));

        // Mod.esp's row puts it first, but Late.esm is a master and a master loads before every non-master.
        var lines = await Lines(game, tree, SkyrimVrMasters, new PluginSortData("Mod.esp", 0, true), new PluginSortData("Late.esm", 1, true));

        lines.Should().Equal("*Late.esm", "*Mod.esp");
        foreach (var name in SkyrimVrMasters)
            await game.Received().ParsePluginFile(Arg.Is<AbsolutePath>(p => p.FileName == name), Arg.Any<RelativePath?>());
    }
}
