using FluentAssertions;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;

namespace SilverModManager.DataModel.Synchronizer.Tests;

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
