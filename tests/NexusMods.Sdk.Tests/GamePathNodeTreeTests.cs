using FluentAssertions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Trees;

namespace NexusMods.Sdk.Tests;

public class GamePathNodeTreeTests
{
    private sealed class Tree(IEnumerable<KeyValuePair<GamePath, int>> items) : AGamePathNodeTree<int>(items);

    private static readonly GamePath Present = new(LocationId.Game, "bin/present.dll");

    [Fact]
    public void Test_Indexer_ReturnsExistingNode()
    {
        var tree = new Tree([KeyValuePair.Create(Present, 1)]);

        tree[Present].Item.Value.Should().Be(1);
    }

    [Fact]
    public void Test_Indexer_ThrowsNamingTheMissingPath()
    {
        var tree = new Tree([KeyValuePair.Create(Present, 1)]);
        var missing = new GamePath(LocationId.Game, "bin/missing.dll");

        var act = () => tree[missing];

        act.Should().Throw<KeyNotFoundException>().WithMessage("*missing.dll*");
    }
}
