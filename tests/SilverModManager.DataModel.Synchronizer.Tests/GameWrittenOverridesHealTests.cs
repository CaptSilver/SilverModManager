using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.StandardGameLocators.TestHelpers;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// A log or crash dump the game writes is dropped from the Overrides once its path is filtered. Its disk state
/// entry is still there then, with no loadout side, and the generic rules would back the file up and delete it.
/// </summary>
[Collection(GameInstalledStubbedGame.CollectionName)]
public class GameWrittenOverridesHealTests : AIsolatedGameTest<GameWrittenOverridesHealTests, GameInstalledStubbedGame>
{
    private static readonly GamePath Log = new(LocationId.Game, "Logs/game.log");
    private static readonly GamePath FilteredOther = new(LocationId.Game, "Catalog/catalog.txt");

    public GameWrittenOverridesHealTests(ITestOutputHelper helper) : base(helper) => GameInstalledStubbedGame.Reset();

    protected override IServiceCollection AddServices(IServiceCollection services) =>
        base.AddServices(services)
            .AddGame<GameInstalledStubbedGame>()
            .AddUniversalGameLocator<GameInstalledStubbedGame>(Version.Parse("0.0.0"));

    private static SyncNodePart Part(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new SyncNodePart { Identity = FileIdentity.XxHash3(bytes.xxHash3()), Size = Size.FromLong(bytes.Length), LastModifiedTicks = 0 };
    }

    private Actions ActionsFor(GamePath path, SyncNode node)
    {
        var tree = new Dictionary<GamePath, SyncNode> { [path] = node };
        Synchronizer.ProcessSyncTree(tree);
        return tree[path].Actions;
    }

    [Fact]
    public void AFilteredLogTheLoadoutNoLongerHoldsIsForgottenNotDeleted()
    {
        GameInstalledStubbedGame.Recognizer.Record.Add(Log);

        // What is left once the Overrides row is dropped: the entry from the last sync, the file unchanged, no loadout side.
        var actions = ActionsFor(Log, new SyncNode { Disk = Part("log 1"), Previous = Part("log 1") });

        actions.Should().Be(Actions.ForgetDiskState, "a file the game wrote is not the app's to delete");
    }

    [Fact]
    public void AFilteredLogAModFileProvidesIsLeftToTheGenericRules()
    {
        GameInstalledStubbedGame.Recognizer.Record.Add(Log);

        var actions = ActionsFor(Log, new SyncNode
        {
            Disk = Part("log 1"), Previous = Part("log 1"), Loadout = Part("log 1"), SourceItemType = LoadoutSourceItemType.Loadout,
        });

        actions.Should().Be(Actions.DoNothing);
    }

    [Fact]
    public void AFilteredFileThatIsNotGameOutputIsLeftToTheGenericRules()
    {
        GameInstalledStubbedGame.Recognizer.Record.Add(FilteredOther);

        var actions = ActionsFor(FilteredOther, new SyncNode { Disk = Part("catalog"), Previous = Part("catalog") });

        actions.Should().NotHaveFlag(Actions.ForgetDiskState);
    }

    [Fact]
    public void ALogOnAPathThatIsNotFilteredIsLeftToTheGenericRules()
    {
        var actions = ActionsFor(Log, new SyncNode { Disk = Part("log 1"), Previous = Part("log 1") });

        actions.Should().NotHaveFlag(Actions.ForgetDiskState);
    }
}
