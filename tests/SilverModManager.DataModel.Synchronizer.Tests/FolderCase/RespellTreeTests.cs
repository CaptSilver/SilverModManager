using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.FolderCase;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.Sdk.Games;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests.FolderCase;

public class RespellTreeTests
{
    private static GamePath G(string path) => new(LocationId.Game, RelativePath.FromUnsanitizedInput(path));

    [Fact]
    public void RespellTree_RewritesKeysToTheChosenSpelling_AndKeepsTheirNodes()
    {
        var spellings = FolderSpellings.Fixed(["Mods", "dlc"]);
        var node = new SyncNode { SourceItemType = LoadoutSourceItemType.Game, Actions = Actions.ExtractToDisk };
        var tree = new Dictionary<GamePath, SyncNode>
        {
            [G("MODS/x/a.ws")] = node,
            [G("Mods/y/b.ws")] = node,
            [G("other/c.txt")] = node,
        };

        ALoadoutSynchronizer.RespellTree(tree, spellings);

        tree.Keys.Select(k => k.Path.ToString()).Order(StringComparer.Ordinal).Should().Equal("Mods/x/a.ws", "Mods/y/b.ws", "other/c.txt");
        tree.Values.Should().OnlyContain(n => n.Actions == Actions.ExtractToDisk);
    }

    [Fact]
    public void RespellTree_WithoutSpellings_ChangesNothing()
    {
        var tree = new Dictionary<GamePath, SyncNode> { [G("MODS/x/a.ws")] = default };

        ALoadoutSynchronizer.RespellTree(tree, null);

        tree.Keys.Single().Path.ToString().Should().Be("MODS/x/a.ws");
    }
}
