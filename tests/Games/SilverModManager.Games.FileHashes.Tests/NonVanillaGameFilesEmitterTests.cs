using System.Collections.Frozen;
using System.Reflection;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.FileHashes.Emitters;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace SilverModManager.Games.FileHashes.Tests;

/// <summary>
/// The notice about non-vanilla game files, read from baseline rows written into an in-memory local store.
/// </summary>
public sealed class NonVanillaGameFilesEmitterTests : IDisposable
{
    private const string GamePath = "/games/test";

    private readonly RealQueryEngineHost _host;
    private readonly IConnection _main;
    private readonly IConnection _local;
    private readonly NonVanillaGameFilesEmitter _emitter;
    private readonly SubstitutableGame _game = Substitute.For<SubstitutableGame>();
    private readonly ILoadoutSynchronizer _synchronizer = Substitute.For<ILoadoutSynchronizer>();

    public NonVanillaGameFilesEmitterTests()
    {
        _host = new RealQueryEngineHost(services => services.AddSingleton<IGameData>(_game));
        _game.Synchronizer.Returns(_synchronizer);
        _synchronizer.IsGameWrittenPath(Arg.Any<GamePath>()).Returns(call => GameWrittenFiles.IsLogOrCrashFile(call.Arg<GamePath>()));
        _main = _host.OpenInMemory("main");
        _local = _host.OpenInMemory("local_hashes");
        var hashes = Substitute.For<IFileHashesService>();
        hashes.Local.Returns(_ => _local.Db);
        _emitter = new NonVanillaGameFilesEmitter(hashes);
    }

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task NoBaseline_YieldsNothing()
    {
        var loadout = await LoadoutFor(GameStore.Steam, GamePath);

        (await Diagnose(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task BaselineWithoutReports_YieldsNothing()
    {
        await HashRows.AddLocalBaseline(_local, GameStore.Steam, GamePath, LocalBaselineKind.DepotManifests, [("a.esm", "vanilla")]);
        var loadout = await LoadoutFor(GameStore.Steam, GamePath);

        (await Diagnose(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task BaselineOfAnotherInstallation_YieldsNothing()
    {
        await HashRows.AddLocalBaseline(_local, GameStore.Steam, "/games/other", LocalBaselineKind.DepotManifests, [], "mod.esp");
        await HashRows.AddLocalBaseline(_local, GameStore.GOG, GamePath, LocalBaselineKind.DepotManifests, [], "mod.esp");
        var loadout = await LoadoutFor(GameStore.Steam, GamePath);

        (await Diagnose(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task GameMoved_ReadsTheBaselineAtTheNewPathOnceTheRowFollows()
    {
        var baseline = await HashRows.AddLocalBaseline(_local, GameStore.Steam, "/mnt/steam/test", LocalBaselineKind.DepotManifests, [("a.esm", "vanilla")]);
        await AddReports(baseline, ("mod.esp", NonVanillaReason.NotInManifest));
        var loadout = await LoadoutFor(GameStore.Steam, GamePath);

        (await Diagnose(loadout)).Should().BeEmpty("the only baseline is at the folder the game moved to");

        using (var tx = _main.BeginTransaction())
        {
            tx.Add(loadout.Installation.Id, GameInstallMetadata.Path, "/mnt/steam/test");
            await tx.Commit();
        }
        loadout = Loadout.Load(_main.Db, loadout.Id);

        (await Diagnose(loadout)).Should().ContainSingle();
    }

    [Fact]
    public async Task BaselineWithReports_YieldsOneDiagnosticListingEachPathAndReason()
    {
        var baseline = await HashRows.AddLocalBaseline(_local, GameStore.Steam, GamePath, LocalBaselineKind.DepotManifests, [("a.esm", "vanilla")]);
        await AddReports(baseline, ("mod.esp", NonVanillaReason.NotInManifest), ("data/other.pak", NonVanillaReason.SizeDiffers), ("b.esm", NonVanillaReason.HashDiffers));
        var loadout = await LoadoutFor(GameStore.Steam, GamePath);

        var diagnostic = (await Diagnose(loadout)).Should().ContainSingle().Subject;

        var data = diagnostic.Should().BeOfType<Diagnostic<Diagnostics.NonVanillaGameFilesMessageData>>().Subject.MessageData;
        data.FileCount.Should().Be(3);
        data.Store.Should().Be(GameStore.Steam.Value);
        data.Game.Should().Be("Test game");
        data.Files.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Should().Equal(
            "* `b.esm` (content differs)",
            "* `data/other.pak` (size differs)",
            "* `mod.esp` (not in the store's manifest)"
        );
    }

    [Fact]
    public async Task FilesTheLoadoutDeploys_AreLeftOut()
    {
        var baseline = await HashRows.AddLocalBaseline(_local, GameStore.Steam, GamePath, LocalBaselineKind.DepotManifests, [("a.esm", "vanilla")]);
        await AddReports(baseline, ("mod.esp", NonVanillaReason.NotInManifest), ("data/deployed.pak", NonVanillaReason.NotInManifest), ("data/replaced.pak", NonVanillaReason.HashDiffers));
        var loadout = await LoadoutFor(GameStore.Steam, GamePath);
        var tree = Tree(
            ("data/deployed.pak", Node(LoadoutSourceItemType.Loadout, hasLoadoutHash: true)),
            ("data/replaced.pak", Node(LoadoutSourceItemType.Loadout, hasLoadoutHash: true))
        );

        var diagnostic = (await Diagnose(loadout, tree)).Should().ContainSingle().Subject;

        var data = diagnostic.Should().BeOfType<Diagnostic<Diagnostics.NonVanillaGameFilesMessageData>>().Subject.MessageData;
        data.FileCount.Should().Be(1);
        data.Files.Should().Contain("mod.esp").And.NotContain("deployed.pak").And.NotContain("replaced.pak");
    }

    [Fact]
    public async Task EveryFileManagedByTheLoadout_YieldsNothing()
    {
        var baseline = await HashRows.AddLocalBaseline(_local, GameStore.Steam, GamePath, LocalBaselineKind.DepotManifests, [("a.esm", "vanilla")]);
        await AddReports(baseline, ("data/a.pak", NonVanillaReason.NotInManifest), ("Data/B.pak", NonVanillaReason.NotInManifest));
        var loadout = await LoadoutFor(GameStore.Steam, GamePath);
        var tree = Tree(
            ("data/a.pak", Node(LoadoutSourceItemType.Loadout, hasLoadoutHash: true)),
            ("data/b.pak", Node(LoadoutSourceItemType.Intrinsic, hasLoadoutHash: false))
        );

        (await Diagnose(loadout, tree)).Should().BeEmpty();
    }

    [Fact]
    public async Task FilesNobodyManages_StayListed()
    {
        // A file the loadout only recorded as part of the game's initial state, a file that is on disk and
        // in no loadout state at all, and a loadout file in another location all leave the report as it was.
        var baseline = await HashRows.AddLocalBaseline(_local, GameStore.Steam, GamePath, LocalBaselineKind.DepotManifests, [("a.esm", "vanilla")]);
        await AddReports(baseline, ("left/over.dll", NonVanillaReason.NotInManifest), ("stray.txt", NonVanillaReason.NotInManifest), ("saves.sav", NonVanillaReason.NotInManifest));
        var loadout = await LoadoutFor(GameStore.Steam, GamePath);
        var tree = new Dictionary<GamePath, SyncNode>(Tree(
            ("left/over.dll", Node(LoadoutSourceItemType.Game, hasLoadoutHash: true)),
            ("stray.txt", Node(LoadoutSourceItemType.Loadout, hasLoadoutHash: false))
        ));
        tree[new GamePath(LocationId.From("Saves"), RelativePath.FromUnsanitizedInput("saves.sav"))] = Node(LoadoutSourceItemType.Loadout, hasLoadoutHash: true);

        var diagnostic = (await Diagnose(loadout, tree.ToFrozenDictionary())).Should().ContainSingle().Subject;

        diagnostic.Should().BeOfType<Diagnostic<Diagnostics.NonVanillaGameFilesMessageData>>().Subject.MessageData.FileCount.Should().Be(3);
    }

    [Fact]
    public async Task FilesIngestedIntoTheOverridesGroup_StayListed()
    {
        // Files found in the game folder are filed in the overrides group as loadout items, so the sync tree
        // carries them like a mod's files. They are the user's own, not something the loadout put there.
        var baseline = await HashRows.AddLocalBaseline(_local, GameStore.Steam, GamePath, LocalBaselineKind.DepotManifests, [("a.esm", "vanilla")]);
        await AddReports(baseline, ("found.dll", NonVanillaReason.NotInManifest), ("from-mod.pak", NonVanillaReason.NotInManifest));
        var loadout = await LoadoutFor(GameStore.Steam, GamePath);
        var (foundItem, modItem) = await AddItems(loadout);
        loadout = Loadout.Load(_main.Db, loadout.Id);
        var tree = Tree(
            ("found.dll", Node(LoadoutSourceItemType.Loadout, hasLoadoutHash: true, foundItem)),
            ("from-mod.pak", Node(LoadoutSourceItemType.Loadout, hasLoadoutHash: true, modItem))
        );

        var diagnostic = (await Diagnose(loadout, tree)).Should().ContainSingle().Subject;

        var data = diagnostic.Should().BeOfType<Diagnostic<Diagnostics.NonVanillaGameFilesMessageData>>().Subject.MessageData;
        data.FileCount.Should().Be(1);
        data.Files.Should().Contain("found.dll");
    }

    [Fact]
    public async Task FilesTheGameInstalled_AreLeftOut_ButAChangedVanillaFileStaysListed()
    {
        // Both are Game-typed nodes with no loadout hash, so the node alone cannot tell them apart: only the
        // game-installed record does.
        var baseline = await HashRows.AddLocalBaseline(_local, GameStore.Steam, GamePath, LocalBaselineKind.DepotManifests, [("a.esm", "vanilla")]);
        await AddReports(baseline, ("data/ccinvented.esm", NonVanillaReason.NotInManifest), ("a.esm", NonVanillaReason.HashDiffers));
        var loadout = await LoadoutFor(GameStore.Steam, GamePath);
        await AddGameInstalledFile(loadout, "data/ccinvented.esm");
        loadout = Loadout.Load(_main.Db, loadout.Id);
        var tree = Tree(
            ("data/ccinvented.esm", Node(LoadoutSourceItemType.Game, hasLoadoutHash: false)),
            ("a.esm", Node(LoadoutSourceItemType.Game, hasLoadoutHash: false))
        );

        var diagnostic = (await Diagnose(loadout, tree)).Should().ContainSingle().Subject;

        var data = diagnostic.Should().BeOfType<Diagnostic<Diagnostics.NonVanillaGameFilesMessageData>>().Subject.MessageData;
        data.FileCount.Should().Be(1);
        data.Files.Should().Contain("a.esm").And.NotContain("ccinvented");
    }

    [Fact]
    public async Task FilesTheGameWritesItself_AreLeftOutOfTheListAndTheCount_AndNamedInAClosingLine()
    {
        var baseline = await HashRows.AddLocalBaseline(_local, GameStore.Steam, GamePath, LocalBaselineKind.DepotManifests, [("a.esm", "vanilla")]);
        await AddReports(baseline, ("mod.esp", NonVanillaReason.NotInManifest), ("bin/game.log", NonVanillaReason.NotInManifest), ("crash.DMP", NonVanillaReason.NotInManifest), ("logs/x.txt", NonVanillaReason.NotInManifest));
        var loadout = await LoadoutFor(GameStore.Steam, GamePath);

        var diagnostic = (await Diagnose(loadout)).Should().ContainSingle().Subject;

        var data = diagnostic.Should().BeOfType<Diagnostic<Diagnostics.NonVanillaGameFilesMessageData>>().Subject.MessageData;
        data.FileCount.Should().Be(1);
        data.Files.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Should().Equal(
            "* `mod.esp` (not in the store's manifest)",
            "3 log and crash files the game wrote itself are not listed."
        );
    }

    [Fact]
    public async Task NothingButFilesTheGameWrites_YieldsNothing()
    {
        var baseline = await HashRows.AddLocalBaseline(_local, GameStore.Steam, GamePath, LocalBaselineKind.DepotManifests, [("a.esm", "vanilla")]);
        await AddReports(baseline, ("bin/game.log", NonVanillaReason.NotInManifest), ("CrashDumps/a.bin", NonVanillaReason.NotInManifest));
        var loadout = await LoadoutFor(GameStore.Steam, GamePath);

        (await Diagnose(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task WhatCountsAsGameWritten_IsTheGamesSynchronizersCall()
    {
        _synchronizer.IsGameWrittenPath(Arg.Any<GamePath>()).Returns(call => call.Arg<GamePath>().Path.ToString() == "bin/own.dat");
        var baseline = await HashRows.AddLocalBaseline(_local, GameStore.Steam, GamePath, LocalBaselineKind.DepotManifests, [("a.esm", "vanilla")]);
        await AddReports(baseline, ("bin/own.dat", NonVanillaReason.NotInManifest), ("game.log", NonVanillaReason.NotInManifest));
        var loadout = await LoadoutFor(GameStore.Steam, GamePath);

        var diagnostic = (await Diagnose(loadout)).Should().ContainSingle().Subject;

        var data = diagnostic.Should().BeOfType<Diagnostic<Diagnostics.NonVanillaGameFilesMessageData>>().Subject.MessageData;
        data.FileCount.Should().Be(1);
        data.Files.Should().Contain("game.log").And.NotContain("own.dat").And.Contain("1 log or crash file the game wrote itself is not listed.");
    }

    [Theory]
    [InlineData("bin/game.log", true)]
    [InlineData("Launcher/debug.LOG", true)]
    [InlineData("crash.dmp", true)]
    [InlineData("logs/output.txt", true)]
    [InlineData("a/Logs/deep/output.txt", true)]
    [InlineData("crashes/report.bin", true)]
    [InlineData("data/CrashDumps/x.bin", true)]
    [InlineData("mod.esp", false)]
    [InlineData("bin/NativeMods/Mod.dll", false)]
    [InlineData("catalog.logic", false)]
    [InlineData("logs", false)]
    [InlineData("backlogs/x.txt", false)]
    public void DefaultRule_CountsLogsAndCrashDumps(string path, bool expected)
    {
        GameWrittenFiles.IsLogOrCrashFile(new GamePath(LocationId.Game, RelativePath.FromUnsanitizedInput(path))).Should().Be(expected);
    }

    private async Task AddGameInstalledFile(Loadout.ReadOnly loadout, string path)
    {
        using var tx = _main.BeginTransaction();
        _ = new GameInstalledGroup.New(tx, out var groupId)
        {
            InstalledForId = loadout.Id,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, groupId)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, groupId) { Name = "Installed by the game", LoadoutId = loadout.Id },
            },
        };
        _ = new GameInstalledFile.New(tx, out var fileId)
        {
            Path = new GamePath(LocationId.Game, RelativePath.FromUnsanitizedInput(path)).ToGamePathParentTuple(loadout.Id),
            Hash = Hash.From(0xAB),
            Size = Size.From(10),
            LoadoutItem = new LoadoutItem.New(tx, fileId) { Name = path, LoadoutId = loadout.Id, ParentId = groupId },
        };
        await tx.Commit();
    }

    [Fact]
    public async Task PlainOverload_UsesNoSyncTree_AndListsEverything()
    {
        var baseline = await HashRows.AddLocalBaseline(_local, GameStore.Steam, GamePath, LocalBaselineKind.DepotManifests, [("a.esm", "vanilla")]);
        await AddReports(baseline, ("mod.esp", NonVanillaReason.NotInManifest));
        var loadout = await LoadoutFor(GameStore.Steam, GamePath);

        var results = new List<Diagnostic>();
        await foreach (var d in _emitter.Diagnose(loadout, CancellationToken.None)) results.Add(d);

        results.Should().ContainSingle();
    }

    [Fact]
    public void DiagnosticIds_AreUniqueWithinTheSource()
    {
        // The template fields throw if read at runtime (they only feed the source generator), so the ids are
        // read from the Create methods the generator emits for them.
        var ids = typeof(Diagnostics)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(m => m.Name.StartsWith("Create", StringComparison.Ordinal) && typeof(Diagnostic).IsAssignableFrom(m.ReturnType))
            .Select(m => ((Diagnostic)m.Invoke(null, m.GetParameters().Select(DefaultArgument).ToArray())!).Id)
            .ToArray();

        ids.Should().HaveCountGreaterThan(2);
        ids.Should().OnlyHaveUniqueItems();
    }

    private static object DefaultArgument(ParameterInfo parameter)
        => parameter.ParameterType == typeof(string) ? ""
            : parameter.ParameterType == typeof(Size) ? Size.From(0)
            : Activator.CreateInstance(parameter.ParameterType)!;

    /// <summary>One item in the loadout's overrides group and one in a mod's group.</summary>
    private async Task<(EntityId InOverrides, EntityId InMod)> AddItems(Loadout.ReadOnly loadout)
    {
        using var tx = _main.BeginTransaction();
        _ = new LoadoutOverridesGroup.New(tx, out var overridesId)
        {
            OverridesForId = loadout.Id,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, overridesId)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, overridesId) { Name = "Overrides", LoadoutId = loadout.Id },
            },
        };
        _ = new LoadoutItemGroup.New(tx, out var modId)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, modId) { Name = "Mod", LoadoutId = loadout.Id },
        };
        var inOverrides = new LoadoutItem.New(tx) { Name = "found.dll", LoadoutId = loadout.Id, ParentId = overridesId };
        var inMod = new LoadoutItem.New(tx) { Name = "from-mod.pak", LoadoutId = loadout.Id, ParentId = modId };
        var result = await tx.Commit();
        return (result[inOverrides.Id], result[inMod.Id]);
    }

    private static SyncNode Node(LoadoutSourceItemType source, bool hasLoadoutHash, EntityId item = default)
    {
        var part = new SyncNodePart { EntityId = item, Hash = hasLoadoutHash ? Hash.From(1) : Hash.Zero, Size = Size.From(1), LastModifiedTicks = 0 };
        return new SyncNode { SourceItemType = source, Loadout = part };
    }

    private static FrozenDictionary<GamePath, SyncNode> Tree(params (string Path, SyncNode Node)[] nodes)
        => nodes.ToFrozenDictionary(n => new GamePath(LocationId.Game, RelativePath.FromUnsanitizedInput(n.Path)), n => n.Node);

    private Task<Diagnostic[]> Diagnose(Loadout.ReadOnly loadout) => Diagnose(loadout, FrozenDictionary<GamePath, SyncNode>.Empty);

    private async Task<Diagnostic[]> Diagnose(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> tree)
    {
        var results = new List<Diagnostic>();
        await foreach (var diagnostic in _emitter.Diagnose(loadout, tree, CancellationToken.None))
            results.Add(diagnostic);
        return results.ToArray();
    }

    private async Task AddReports(EntityId baseline, params (string Path, NonVanillaReason Reason)[] reports)
    {
        using var tx = _local.BeginTransaction();
        foreach (var (path, reason) in reports)
        {
            _ = new NonVanillaFile.New(tx)
            {
                BaselineId = baseline,
                Path = RelativePath.FromUnsanitizedInput(path),
                Reason = reason,
                Size = Size.From(1),
            };
        }
        await tx.Commit();
    }

    private async Task<Loadout.ReadOnly> LoadoutFor(GameStore store, string gamePath)
    {
        var id = await HashRows.AddLoadout(_main, store, gamePath);
        return Loadout.Load(_main.Db, id);
    }
}
