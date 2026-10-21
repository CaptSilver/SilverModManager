using System.Collections.Frozen;
using System.Reflection;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Games.FileHashes;
using NexusMods.Abstractions.Games.FileHashes.Models;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.FileHashes.Emitters;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NSubstitute;

namespace NexusMods.Games.FileHashes.Tests;

/// <summary>
/// The notice about non-vanilla game files, read from baseline rows written into an in-memory local store.
/// </summary>
public sealed class NonVanillaGameFilesEmitterTests : IDisposable
{
    private const string GamePath = "/games/test";

    private readonly RealQueryEngineHost _host = new();
    private readonly IConnection _main;
    private readonly IConnection _local;
    private readonly NonVanillaGameFilesEmitter _emitter;

    public NonVanillaGameFilesEmitterTests()
    {
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
