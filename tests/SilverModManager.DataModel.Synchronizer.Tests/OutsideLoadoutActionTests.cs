using FluentAssertions;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Abstractions.Loadouts.Synchronizers.Rules;
using NexusMods.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;

namespace NexusMods.DataModel.Synchronizer.Tests;

/// <summary>
/// The installation overload of RunActions has no loadout, so actions that only make sense inside one are refused.
/// </summary>
public class OutsideLoadoutActionTests(ITestOutputHelper helper) : AStubbedGameIsolatedTest<OutsideLoadoutActionTests>(helper)
{
    [Fact]
    public async Task WriteIntrinsicOutsideALoadoutIsRefusedAsWriteIntrinsic()
    {
        // The check only runs in debug builds (or with the debug switch set).
        ApplicationConstants.IsDebug.Should().BeTrue();

        await LoadoutManager.ManageInstallation(GameInstallation);

        // Only WriteIntrinsic is set: a check that looks at the wrong flag lets this tree through.
        var tree = new Dictionary<GamePath, SyncNode>
        {
            [new GamePath(LocationId.Game, "intrinsic.bin")] = new()
            {
                Disk = new SyncNodePart { Hash = Hash.From(0xA1), Size = Size.From(1), LastModifiedTicks = 1 },
                Actions = Actions.WriteIntrinsic,
            },
        };

        var act = () => ((ALoadoutSynchronizer)Synchronizer).RunActions(tree, GameInstallation);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("write intrinsic");
    }
}
