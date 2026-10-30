using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.FileHashes;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// A node whose present parts carry identities of different kinds cannot say whether they match until the file is
/// read; ProcessSyncTree flags it so the caller can resolve it.
/// </summary>
public class IdentityUnresolvedTests(ITestOutputHelper testOutputHelper) : ACyberpunkIsolatedGameTest<IdentityUnresolvedTests>(testOutputHelper)
{
    private static readonly GamePath Path = new(LocationId.Game, "bin/x64/identity.txt");

    private static readonly FileIdentity Xx1 = FileIdentity.XxHash3(Hash.From(0xFAD01));
    private static readonly FileIdentity Xx2 = FileIdentity.XxHash3(Hash.From(0xFAD02));
    private static readonly FileIdentity Sha1 = FileIdentity.Sha1(Sha1Value.FromHex(new string('A', 40)));

    private static SyncNodePart Part(FileIdentity identity) => new() { Identity = identity, Size = Size.FromLong(10), LastModifiedTicks = 5 };

    private bool Unresolved(SyncNode node)
    {
        var tree = new Dictionary<GamePath, SyncNode> { [Path] = node };
        Synchronizer.ProcessSyncTree(tree);
        return tree[Path].IdentityUnresolved;
    }

    [Fact]
    public void DiskAndLoadoutOfDifferentKinds_AreUnresolved() =>
        Unresolved(new SyncNode { Disk = Part(Sha1), Loadout = Part(Xx1) }).Should().BeTrue();

    [Fact]
    public void PreviousAndLoadoutOfDifferentKinds_AreUnresolved() =>
        Unresolved(new SyncNode { Previous = Part(Sha1), Loadout = Part(Xx1) }).Should().BeTrue();

    [Fact]
    public void DiskAndPreviousOfDifferentKinds_AreUnresolved() =>
        Unresolved(new SyncNode { Disk = Part(Sha1), Previous = Part(Xx1) }).Should().BeTrue();

    [Fact]
    public void PartsOfTheSameKind_AreResolvedWhetherTheyMatchOrNot()
    {
        Unresolved(new SyncNode { Disk = Part(Xx1), Previous = Part(Xx1), Loadout = Part(Xx1) }).Should().BeFalse();
        Unresolved(new SyncNode { Disk = Part(Xx1), Previous = Part(Xx1), Loadout = Part(Xx2) }).Should().BeFalse();
    }

    [Fact]
    public void AMissingPart_IsNeverUnresolved()
    {
        Unresolved(new SyncNode { Disk = Part(Sha1) }).Should().BeFalse();
        Unresolved(new SyncNode { Disk = Part(Sha1), Previous = Part(Sha1) }).Should().BeFalse();
    }

    [Fact]
    public void ATreeOfUnrecordedMixedNodes_AsksTheIndexPerKindNotPerNode()
    {
        const int nodes = 400;
        var tree = new Dictionary<GamePath, SyncNode>();
        for (var i = 0; i < nodes; i++)
        {
            var sha1 = FileIdentity.Sha1(Sha1Value.FromHex(i.ToString("x8").PadLeft(40, 'A')));
            var xxHash3 = FileIdentity.XxHash3(Hash.From(0xBEEF0000UL + (ulong)i));
            tree[new GamePath(LocationId.Game, $"bin/x64/unrecorded{i}.dat")] = new SyncNode { Disk = Part(sha1), Previous = Part(sha1), Loadout = Part(xxHash3) };
        }

        var resolver = (RecordedDigestResolver)ServiceProvider.GetRequiredService<IFileIdentityResolver>();
        var before = resolver.QueriesIssued;

        Synchronizer.ProcessSyncTree(tree);

        tree.Values.Should().OnlyContain(n => n.IdentityUnresolved);
        // Two kinds (SHA-1 and xxHash3) in each of the two hash databases the service lists.
        (resolver.QueriesIssued - before).Should().BeLessThanOrEqualTo(4);
    }
}
