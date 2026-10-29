using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Benchmarks.Interfaces;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.MnemonicDB.Abstractions;
using Xunit;

namespace SilverModManager.Benchmarks.Benchmarks.Loadouts;

/// <summary>
/// Builds and processes a sync tree of 50 000 nodes in which a share of the nodes has a store-vouched (SHA-1)
/// disk part beside an xxHash3 loadout part, with or without a recorded relation joining the two. It goes through the
/// public tree API only, so the same file measures a build from before and after the identity work.
/// </summary>
[BenchmarkInfo("Sync tree build", "Builds and processes a 50k node sync tree, by share of nodes whose disk part is named by another hash kind than the loadout part, with and without a recorded relation.")]
[MemoryDiagnoser]
[Config(typeof(InProcessShortRunConfig))]
public class SyncTreeBuild() : ACyberpunkIsolatedGameTest<SyncTreeBuild>(new NullOutput()), IBenchmark
{
    private const int NodeCount = 50_000;

    private Loadout.ReadOnly _loadout;
    private PathPartPair[] _disk = [];

    [Params(0, 5, 100)]
    public int MixedPercent { get; set; }

    /// <summary>
    /// Whether the mixed nodes have a recorded relation. Without one the tree can only call them unresolved, and
    /// the process step asks the recorded digests about every pair.
    /// </summary>
    [Params(true, false)]
    public bool Related { get; set; }

    [GlobalSetup]
    public void Setup() => SetupAsync().GetAwaiter().GetResult();

    [GlobalCleanup]
    public void Cleanup() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private async Task SetupAsync()
    {
        await InitializeAsync();
        _loadout = await CreateLoadout();

        var paths = new GamePath[NodeCount];
        var contents = new byte[NodeCount][];
        for (var i = 0; i < NodeCount; i++)
        {
            paths[i] = new GamePath(LocationId.Game, $"bin/x64/pack{i / 100}/file{i}.dat");
            contents[i] = Encoding.UTF8.GetBytes(paths[i].Path.ToString());
        }

        var mixedCount = NodeCount * MixedPercent / 100;
        var disk = new PathPartPair[NodeCount];
        var multiHashes = new MultiHash[mixedCount];
        for (var i = 0; i < mixedCount; i++)
        {
            multiHashes[i] = await MultiHasher.HashStream(new MemoryStream(contents[i]));
        }

        using (var tx = Connection.BeginTransaction())
        {
            var group = AddEmptyGroup(tx, _loadout, "Bench");
            for (var i = 0; i < NodeCount; i++)
            {
                AddFile(tx, _loadout, group, paths[i], null, out var xxHash3, out var size);
                var identity = FileIdentity.XxHash3(xxHash3);
                if (i < mixedCount)
                {
                    identity = FileIdentity.Sha1(multiHashes[i].Sha1);
                }

                disk[i] = new PathPartPair(paths[i], new SyncNodePart { Identity = identity, Size = size, LastModifiedTicks = 1 });
            }

            await tx.Commit();
        }

        var local = ((StubbedFileHasherService)ServiceProvider.GetRequiredService<IFileHashesService>()).LocalConnection;
        using (var tx = local.BeginTransaction())
        {
            for (var i = 0; Related && i < mixedCount; i++)
            {
                var hashes = multiHashes[i];
                _ = new HashRelation.New(tx)
                {
                    MinimalHash = hashes.MinimalHash,
                    XxHash3 = hashes.XxHash3,
                    Sha1 = hashes.Sha1,
                    XxHash64 = hashes.XxHash64,
                    Md5 = hashes.Md5,
                    Size = hashes.Size,
                    Crc32 = hashes.Crc32,
                };
            }

            await tx.Commit();
        }

        Refresh(ref _loadout);
        _disk = disk;
    }

    [Benchmark]
    public int Build() => Synchronizer.BuildSyncTree(_disk, _disk, _loadout).Count;

    [Benchmark]
    public int BuildAndProcess()
    {
        var tree = Synchronizer.BuildSyncTree(_disk, _disk, _loadout);
        Synchronizer.ProcessSyncTree(tree);
        return tree.Count;
    }

    private sealed class NullOutput : ITestOutputHelper
    {
        public string Output => string.Empty;
        public void Write(string message) { }
        public void Write(string format, params object[] args) { }
        public void WriteLine(string message) { }
        public void WriteLine(string format, params object[] args) { }
    }
}
