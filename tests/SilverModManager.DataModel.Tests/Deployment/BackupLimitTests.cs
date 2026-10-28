using SilverModManager.Sdk.Hashes;
using FluentAssertions;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.DataModel.Tests.Deployment;

public class BackupLimitTests(ITestOutputHelper helper) : AStubbedGameIsolatedTest<BackupLimitTests>(helper)
{
    private static SyncNode BackupNode(ulong hash, Size size) => new()
    {
        Disk = new SyncNodePart { Identity = FileIdentity.XxHash3(Hash.From(hash)), Size = size, LastModifiedTicks = 1 },
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

        // The question is settled before the synchronizer lock is taken or a file is read; the default prompt refuses.
        var act = () => ((ALoadoutSynchronizer)Synchronizer).ResolveBackupOverflow(tree, GameInstallation, preset: null, CancellationToken.None);

        (await act.Should().ThrowAsync<BackupOverflowException>()).Which.Message.Should().Contain("6 GB").And.Contain("--large-files backup");
    }
}
