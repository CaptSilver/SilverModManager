using System.Collections.Frozen;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.Games.FileHashes.Emitters;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FileHashes.Tests;

/// <summary>
/// The diagnostic for game files the sync tree cannot deploy, which says different things about a file that
/// is not on disk and a file that is on disk with a hash the app's file data does not list.
/// </summary>
public sealed class MissingGameFilesEmitterTests : IDisposable
{
    private static readonly Hash Expected = Hash.From(0x1111);
    private static readonly Hash OnDisk = Hash.From(0x2222);

    private readonly RealQueryEngineHost _host = new();
    private readonly IConnection _main;
    private readonly UndeployableLoadoutDueToMissingGameFiles _emitter;

    public MissingGameFilesEmitterTests()
    {
        _main = _host.OpenInMemory("main");
        var services = new ServiceCollection().AddSingleton(_main).BuildServiceProvider();
        _emitter = new UndeployableLoadoutDueToMissingGameFiles(services);
    }

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task AGameFileAbsentFromDiskIsReportedAsMissing()
    {
        var tree = Tree(("data/absent.pak", Node(disk: null)));

        var diagnostic = (await Diagnose(tree)).Should().ContainSingle().Subject;

        var data = diagnostic.Should().BeOfType<Diagnostic<Diagnostics.UndeployableLoadoutDueToMissingGameFilesMessageData>>().Subject.MessageData;
        data.FileCount.Should().Be(1);
        data.Files.Should().Contain("data/absent.pak");
    }

    [Fact]
    public async Task AGameFileOnDiskWithAnotherHashIsReportedAsADataMismatch()
    {
        var tree = Tree(("data/changed.pak", Node(disk: OnDisk)));

        var diagnostic = (await Diagnose(tree)).Should().ContainSingle().Subject;

        var data = diagnostic.Should().BeOfType<Diagnostic<Diagnostics.GameFilesDifferFromTheAppsFileDataMessageData>>().Subject.MessageData;
        data.FileCount.Should().Be(1);
        data.Files.Should().Contain("data/changed.pak");
    }

    [Fact]
    public async Task MissingAndMismatchedFilesAreEachListedUnderTheirOwnDiagnostic()
    {
        var tree = Tree(
            ("data/absent.pak", Node(disk: null)),
            ("data/changed.pak", Node(disk: OnDisk)),
            ("data/also_absent.pak", Node(disk: null))
        );

        var diagnostics = await Diagnose(tree);

        diagnostics.Should().HaveCount(2);
        var missing = diagnostics.OfType<Diagnostic<Diagnostics.UndeployableLoadoutDueToMissingGameFilesMessageData>>().Single().MessageData;
        missing.FileCount.Should().Be(2);
        missing.Files.Should().Contain("data/absent.pak").And.Contain("data/also_absent.pak").And.NotContain("changed.pak");
        var mismatched = diagnostics.OfType<Diagnostic<Diagnostics.GameFilesDifferFromTheAppsFileDataMessageData>>().Single().MessageData;
        mismatched.FileCount.Should().Be(1);
        mismatched.Files.Should().Contain("data/changed.pak");
    }

    [Fact]
    public async Task FilesThatNeedNoWarningYieldNothing()
    {
        var tree = Tree(
            ("data/fine.pak", Node(disk: Expected, actions: Actions.DoNothing)),
            ("data/mod.pak", Node(disk: null, source: LoadoutSourceItemType.Loadout))
        );

        (await Diagnose(tree)).Should().BeEmpty();
    }

    private static SyncNodePart Part(Hash hash) => new() { Hash = hash, Size = Size.From(10), LastModifiedTicks = 0 };

    private static SyncNode Node(Hash? disk, Actions actions = Actions.WarnOfUnableToExtract, LoadoutSourceItemType source = LoadoutSourceItemType.Game)
    {
        var node = new SyncNode { Loadout = Part(Expected), SourceItemType = source, Actions = actions };
        if (disk is { } hash) node.Disk = Part(hash);
        return node;
    }

    private static FrozenDictionary<GamePath, SyncNode> Tree(params (string Path, SyncNode Node)[] nodes)
        => nodes.ToFrozenDictionary(n => new GamePath(LocationId.Game, RelativePath.FromUnsanitizedInput(n.Path)), n => n.Node);

    private async Task<Diagnostic[]> Diagnose(FrozenDictionary<GamePath, SyncNode> tree)
    {
        var id = await HashRows.AddLoadout(_main, GameStore.Steam, "/games/test");
        var loadout = Loadout.Load(_main.Db, id);
        var results = new List<Diagnostic>();
        await foreach (var diagnostic in _emitter.Diagnose(loadout, tree, CancellationToken.None))
            results.Add(diagnostic);
        return results.ToArray();
    }
}
