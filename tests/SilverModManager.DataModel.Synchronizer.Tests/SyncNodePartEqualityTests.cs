using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Sdk.Hashes;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

public class SyncNodePartEqualityTests
{
    private static SyncNodePart Part(ulong hash) => new()
    {
        Identity = FileIdentity.XxHash3(Hash.From(hash)),
        Size = Size.FromLong(10),
        LastModifiedTicks = 5,
    };

    [Fact]
    public void PartsWithTheSameFields_AreEqual()
    {
        Part(1).Equals(Part(1)).Should().BeTrue();
        Part(1).Equals(Part(2)).Should().BeFalse();
    }

    [Fact]
    public void NodesBuiltFromEqualParts_AreEqual()
    {
        var left = new SyncNode { Disk = Part(1), Loadout = Part(2) };
        var right = new SyncNode { Disk = Part(1), Loadout = Part(2) };
        left.Should().Be(right);
    }

    [Fact]
    public void PartEquality_DoesNotFallBackToValueTypeReflection()
    {
        typeof(SyncNodePart).GetMethod("Equals", [typeof(SyncNodePart)]).Should().NotBeNull();
        typeof(SyncNodePart).Should().Implement<IEquatable<SyncNodePart>>();
    }
}
