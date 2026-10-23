using FluentAssertions;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Abstractions.Loadouts.Synchronizers.Rules;
using NexusMods.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.DataModel.Tests.Deployment;

public class BackupLimitTests(ITestOutputHelper helper) : AStubbedGameIsolatedTest<BackupLimitTests>(helper)
{
    private static SyncNode BackupNode(ulong hash, Size size) => new()
    {
        Disk = new SyncNodePart { Hash = Hash.From(hash), Size = size, LastModifiedTicks = 1 },
        Actions = Actions.BackupFile,
    };

    [Fact]
    public async Task FilesToBackUpOverTheLimitTogetherFailBeforeAnyFileIsRead()
    {
        await LoadoutManager.ManageInstallation(GameInstallation);
        var loadout = await CreateLoadout();

        // Neither file exists on disk and neither is over the limit alone: only the total can refuse, and
        // only before the first read.
        var tree = new Dictionary<GamePath, SyncNode>
        {
            [new GamePath(LocationId.Game, "a.bin")] = BackupNode(0xA1, Size.GB * 3),
            [new GamePath(LocationId.Preferences, "b.bin")] = BackupNode(0xB2, Size.GB * 3),
        };

        var act = () => ((ALoadoutSynchronizer)Synchronizer).RunActions(tree, loadout);

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("larger than the maximum");
    }
}
