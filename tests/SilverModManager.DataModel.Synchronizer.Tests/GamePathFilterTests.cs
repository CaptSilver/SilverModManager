using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

public class GamePathFilterTests(ITestOutputHelper helper) : AStubbedGameTest<GamePathFilterTests>(helper)
{
    [Theory]
    [InlineData("a/b.sav")]
    [InlineData("")]
    public void SavesLocation_IsFilteredForEveryPath(string path)
    {
        Synchronizer.GamePathFilter.ShouldFilter(new GamePath(LocationId.Saves, path)).Should().BeTrue();
    }

    [Theory]
    [InlineData("Game", "a.dll")]
    [InlineData("Preferences", "x.ini")]
    public void OtherLocations_AreNotFiltered(string location, string path)
    {
        Synchronizer.GamePathFilter.ShouldFilter(new GamePath(LocationId.From(location), path)).Should().BeFalse();
    }

    [Theory]
    [InlineData("Game", "vkd3d-proton.cache")]
    [InlineData("Game", "vkd3d-proton.cache.write.0")]
    [InlineData("Game", "bin/x64/Cyberpunk2077.dxvk-cache")]
    [InlineData("AppData", "vkd3d-proton.cache")]
    public void ProtonShaderCaches_AreFiltered(string location, string path)
    {
        Synchronizer.GamePathFilter.ShouldFilter(new GamePath(LocationId.From(location), path)).Should().BeTrue();
    }

    [Theory]
    [InlineData("Game", "cache.txt")]
    [InlineData("Game", "bin/vkd3d-proton.cache.txt/readme.md")]
    public void SimilarlyNamedFiles_AreNotFiltered(string location, string path)
    {
        Synchronizer.GamePathFilter.ShouldFilter(new GamePath(LocationId.From(location), path)).Should().BeFalse();
    }

    private static readonly GamePath Save = new(LocationId.Saves, "slot1.sav");
    private static readonly FileIdentity Xx1 = FileIdentity.XxHash3(Hash.From(0xFAD01));
    private static readonly FileIdentity Xx2 = FileIdentity.XxHash3(Hash.From(0xFAD02));
    private static readonly FileIdentity Sha1 = FileIdentity.Sha1(Sha1Value.FromHex(new string('A', 40)));

    private static SyncNodePart Part(FileIdentity identity) => new() { Identity = identity, Size = Size.FromLong(10), LastModifiedTicks = 5 };

    private Actions ActionsOf(SyncNode node)
    {
        var tree = new Dictionary<GamePath, SyncNode> { [Save] = node };
        Synchronizer.ProcessSyncTree(tree);
        return tree[Save].Actions;
    }

    [Fact]
    public void ASavePreviousOfAnotherKindThanTheDisk_IsForgottenNotOverwritten() =>
        ActionsOf(new SyncNode { Disk = Part(Sha1), Previous = Part(Xx1), Loadout = Part(Xx1) }).Should().Be(Actions.ForgetDiskState);

    [Fact]
    public void ASavePreviousOfOtherBytesThanTheDisk_IsForgottenNotOverwritten() =>
        ActionsOf(new SyncNode { Disk = Part(Xx2), Previous = Part(Xx1), Loadout = Part(Xx1) }).Should().Be(Actions.ForgetDiskState);

    [Fact]
    public void ASavePreviousThatIsTheDisk_IsNotForgotten() =>
        ActionsOf(new SyncNode { Disk = Part(Xx1), Previous = Part(Xx1), Loadout = Part(Xx1) }).Should().NotBe(Actions.ForgetDiskState);
}
