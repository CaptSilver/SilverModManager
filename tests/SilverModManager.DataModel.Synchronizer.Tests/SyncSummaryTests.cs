using FluentAssertions;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.Sdk.Games;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

public class SyncSummaryTests
{
    private static readonly GamePath DataRoot = new(LocationId.Game, "Data");

    private static readonly IReadOnlyDictionary<GamePath, SyncNode> Tree = new Dictionary<GamePath, SyncNode>();

    private static string Describe(params GamePath[] mountedRoots)
        => SyncSummary.Describe("Sync of loadout Main", Tree, [(DeploymentBackendId.Fuse, Tree)], foldersRemoved: 0, mountedRoots);

    [Fact]
    public void AFuseRootAddsTheMountedFolderLine() =>
        Describe(DataRoot).Should().EndWith("Files under Game/Data appear only when the game or a tool is started from SilverModManager.");

    [Fact]
    public void WithoutAFuseRootTheLineIsUnchanged() =>
        Describe().Should().NotContain("appear only when");

    [Fact]
    public void TheLineFollowsTheDeletionsSentence()
    {
        var deleted = new Dictionary<GamePath, SyncNode>
        {
            [new GamePath(LocationId.Game, "a/b.txt")] = new() { Actions = Actions.DeleteFromDisk },
        };

        var line = SyncSummary.Describe("Sync", deleted, [(DeploymentBackendId.Copy, deleted)], 0, [DataRoot]);

        line.Should().Contain("Most deletions in ").And.EndWith(". Files under Game/Data appear only when the game or a tool is started from SilverModManager.");
    }
}
