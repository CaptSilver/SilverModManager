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

    /// <summary>
    /// Has no vanilla copy, so the merge is two-way and never reads the game folder, which the engine tests share
    /// and rewrite while they run in parallel.
    /// </summary>
    private sealed class NoVanillaGame : TestMergeGame
    {
        public override GamePath? VanillaFor(string rel, GameInstallation installation) => null;
    }

    private static readonly GamePath Good = new(LocationId.Game, "Mods/merged/scripts/part.txt");
    private static readonly GamePath Bad = new(LocationId.Game, "Mods/merged/scripts/part-bad.txt");

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

    private async Task<(MergeSynchronizerPart Part, MergeEngine Engine, Loadout.ReadOnly Loadout)> FirstApplyWithOneFailingPath()
    {
        var loadout = await CreateLoadout();
        await Mod(loadout, "A", "modA", 2, ("scripts/part.txt", Text("a\nB1\nc\n")), ("scripts/part-bad.txt", [0xFF, 0xFE]));
        await Mod(loadout, "B", "modB", 1, ("scripts/part.txt", Text("a\nb\nC\n")), ("scripts/part-bad.txt", Text("text\n")));
        var engine = new MergeEngine(FileStore, new Trusting(), NullLogger<MergeEngine>.Instance, _ => new NoVanillaGame());
        return (new MergeSynchronizerPart(engine), engine, Refresh(loadout));
    }

    [Fact]
    public async Task AFailedMergeIsNotListedOnceThePreflightHasPlanned()
    {
        var (part, _, loadout) = await FirstApplyWithOneFailingPath();

        // Before any plan the engine cannot know bad.txt fails, which is why the preflight plans first.
        part.IntrinsicFiles(loadout, NullLogger.Instance).Keys.Should().BeEquivalentTo([Good, Bad]);

        part.PlanOutputs(loadout);
        var files = part.IntrinsicFiles(loadout, NullLogger.Instance);

        files.Keys.Should().BeEquivalentTo([Good]);
        var stream = new MemoryStream();
        await files[Good].Write(stream, loadout, []);
        Encoding.UTF8.GetString(stream.ToArray()).Should().Be("a\nB1\nc\n");
    }

    [Fact]
    public async Task CheckOutputsPassesATreeWithoutTheFailedPathSoTheApplyGoesAhead()
    {
        var (part, _, loadout) = await FirstApplyWithOneFailingPath();
        part.PlanOutputs(loadout);
        var tree = part.IntrinsicFiles(loadout, NullLogger.Instance).Keys
            .ToDictionary(p => p, _ => new SyncNode { SourceItemType = LoadoutSourceItemType.Intrinsic });

        var act = () => part.CheckOutputs(tree, loadout);

        act.Should().NotThrow();
    }

    [Fact]
    public async Task ATreeBuiltFromAListingThatWasNeverPlannedIsRefusedNotLeftToCrashTheWrite()
    {
        var (part, _, loadout) = await FirstApplyWithOneFailingPath();
        var tree = part.IntrinsicFiles(loadout, NullLogger.Instance).Keys
            .ToDictionary(p => p, _ => new SyncNode { SourceItemType = LoadoutSourceItemType.Intrinsic });

        var act = () => part.CheckOutputs(tree, loadout);

        act.Should().Throw<SynchronizerPreflightException>().WithMessage("*changed while it was being applied*");
    }
}
