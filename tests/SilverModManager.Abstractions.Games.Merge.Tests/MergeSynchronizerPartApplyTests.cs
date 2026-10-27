using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;
using SilverModManager.Abstractions.Games.Merge.Tests.Fixtures;

namespace SilverModManager.Abstractions.Games.Merge.Tests;

/// <summary>The part against the real engine: what the first apply lists once the preflight has planned.</summary>
public class MergeSynchronizerPartApplyTests(IServiceProvider provider) : AGameTest<StubbedGame>(provider)
{
    private sealed class Trusting : IVanillaTrust
    {
        public bool IsTrusted(GameInstallation installation, GamePath path, Hash currentHash, out string reason)
        {
            reason = "";
            return true;
        }
    }

    private async Task Mod(Loadout.ReadOnly loadout, string name, string package, ulong priority, params (string Rel, byte[] Bytes)[] files)
    {
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, name);
        foreach (var (rel, bytes) in files)
        {
            var path = new GamePath(LocationId.Game, $"Mods/{package}/{rel}");
            AddFile(tx, loadout.LoadoutId, group, path, bytes, out var hash, out var size);
            await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(path.Path, new MemoryStream(bytes)), hash, size)]);
        }
        _ = new LoadoutItemGroupPriority.New(tx)
        {
            LoadoutId = loadout.LoadoutId,
            TargetId = group,
            Priority = ConflictPriority.From(priority),
        };
        await tx.Commit();
    }

    private static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);

    private async Task<(MergeSynchronizerPart Part, Loadout.ReadOnly Loadout, GamePath Good, GamePath Bad)> FirstApplyWithOneFailingPath()
    {
        var loadout = await CreateLoadout();
        // A rel of its own: the engine tests share the game folder and rewrite their vanilla files while running in parallel.
        var rel = $"scripts/part-{Guid.NewGuid():N}.txt";
        var badRel = $"scripts/bad-{Guid.NewGuid():N}.txt";
        var vanilla = GameInstallation.Locations[LocationId.Game].Path.Combine($"vanilla/{rel}");
        vanilla.Parent.CreateDirectory();
        await vanilla.WriteAllTextAsync("a\nb\nc\n");
        await Mod(loadout, "A", "modA", 2, (rel, Text("a\nB1\nc\n")), (badRel, [0xFF, 0xFE]));
        await Mod(loadout, "B", "modB", 1, (rel, Text("a\nb\nC\n")), (badRel, Text("text\n")));
        var engine = new MergeEngine(FileStore, new Trusting(), NullLogger<MergeEngine>.Instance, _ => new TestMergeGame());
        return (new MergeSynchronizerPart(engine), Refresh(loadout), new GamePath(LocationId.Game, $"Mods/merged/{rel}"), new GamePath(LocationId.Game, $"Mods/merged/{badRel}"));
    }

    [Fact]
    public async Task AnApplyFoldsEachPathOnceAndItsWritesPlanNothing()
    {
        var loadout = await CreateLoadout();
        var rels = Enumerable.Range(0, 3).Select(i => $"scripts/count-{i}-{Guid.NewGuid():N}.txt").ToArray();
        foreach (var rel in rels)
        {
            var vanilla = GameInstallation.Locations[LocationId.Game].Path.Combine($"vanilla/{rel}");
            vanilla.Parent.CreateDirectory();
            await vanilla.WriteAllTextAsync("a\nb\nc\n");
        }
        await Mod(loadout, "A", "modA", 2, rels.Select(r => (r, Text("a\nB1\nc\n"))).ToArray());
        await Mod(loadout, "B", "modB", 1, rels.Select(r => (r, Text("a\nb\nC\n"))).ToArray());
        loadout = Refresh(loadout);
        var strategy = new CountingStrategy();
        var game = new CountingMergeGame { Strategy = strategy };
        var part = new MergeSynchronizerPart(new MergeEngine(FileStore, new Trusting(), NullLogger<MergeEngine>.Instance, _ => game));

        // What one apply does: plan in the loadout preflight, list, build the tree, check it, write every merged file.
        part.PlanOutputs(loadout);
        var files = part.IntrinsicFiles(loadout, NullLogger.Instance);
        var tree = files.Keys.ToDictionary(p => p, _ => new SyncNode { SourceItemType = LoadoutSourceItemType.Intrinsic });
        part.CheckOutputs(tree, loadout);
        var scansBeforeTheWrites = game.MergesCalls;

        foreach (var file in files.Values)
        {
            var stream = new MemoryStream();
            await file.Write(stream, loadout, tree);
            Encoding.UTF8.GetString(stream.ToArray()).Should().Be("a\nB1\nC\n");
        }

        files.Should().HaveCount(rels.Length);
        strategy.Folds.Should().Be(rels.Length);
        game.MergesCalls.Should().Be(scansBeforeTheWrites, "the writes copy the plan the check verified instead of scanning the loadout again");
    }

    [Fact]
    public async Task AFailedMergeIsNotListedOnceThePreflightHasPlanned()
    {
        var (part, loadout, good, bad) = await FirstApplyWithOneFailingPath();

        // Before any plan the engine cannot know the bad file fails, which is why the preflight plans first.
        part.IntrinsicFiles(loadout, NullLogger.Instance).Keys.Should().BeEquivalentTo([good, bad]);

        part.PlanOutputs(loadout);
        var files = part.IntrinsicFiles(loadout, NullLogger.Instance);

        files.Keys.Should().BeEquivalentTo([good]);
        var stream = new MemoryStream();
        await files[good].Write(stream, loadout, []);
        Encoding.UTF8.GetString(stream.ToArray()).Should().Be("a\nB1\nC\n");
    }

    [Fact]
    public async Task CheckOutputsPassesATreeWithoutTheFailedPathSoTheApplyGoesAhead()
    {
        var (part, loadout, good, bad) = await FirstApplyWithOneFailingPath();
        part.PlanOutputs(loadout);
        var tree = part.IntrinsicFiles(loadout, NullLogger.Instance).Keys
            .ToDictionary(p => p, _ => new SyncNode { SourceItemType = LoadoutSourceItemType.Intrinsic });

        var act = () => part.CheckOutputs(tree, loadout);

        act.Should().NotThrow();
    }

    [Fact]
    public async Task ATreeBuiltFromAListingThatWasNeverPlannedIsRefusedNotLeftToCrashTheWrite()
    {
        var (part, loadout, good, bad) = await FirstApplyWithOneFailingPath();
        var tree = part.IntrinsicFiles(loadout, NullLogger.Instance).Keys
            .ToDictionary(p => p, _ => new SyncNode { SourceItemType = LoadoutSourceItemType.Intrinsic });

        var act = () => part.CheckOutputs(tree, loadout);

        act.Should().Throw<SynchronizerPreflightException>().WithMessage("*changed while it was being applied*");
    }
}
