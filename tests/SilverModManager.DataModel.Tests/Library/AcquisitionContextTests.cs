using FluentAssertions;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using Xunit;

namespace SilverModManager.DataModel.Tests.Library;

public class AcquisitionContextTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<AcquisitionContextTests>(helper)
{
    private static readonly GameId GameA = GameId.From("acquisition-test-a");
    private static readonly GameId GameB = GameId.From("acquisition-test-b");
    private static readonly GameId GameC = GameId.From("acquisition-test-c");

    private async Task<LibraryItemId> AddItem(Destination destination)
    {
        using var tx = Connection.BeginTransaction();
        var temp = tx.TempId();
        _ = new LibraryItem.New(tx, temp) { Name = Guid.NewGuid().ToString() };
        LibraryDestinations.Write(tx, temp, destination);
        var result = await tx.Commit();
        return LibraryItemId.From(result[temp]);
    }

    [Fact]
    public void GameAcquisition_AnswersThatGame()
        => new GameAcquisition(GameA).Destination(Connection.Db).Should().Be(Destination.Of(GameA));

    [Fact]
    public void UnassignedAcquisition_AnswersUnknown()
        => UnassignedAcquisition.Instance.Destination(Connection.Db).IsUnknown.Should().BeTrue();

    [Fact]
    public async Task LoadoutAcquisition_AnswersTheLoadoutsGame()
    {
        var loadout = await CreateLoadout();

        new LoadoutAcquisition(loadout.LoadoutId).Destination(Connection.Db)
            .Should().Be(Destination.Of(GameInstallation.Game.GameId));
    }

    [Fact]
    public async Task LoadoutAcquisition_InstallationWithoutGame_IsUnknown()
    {
        var loadout = await CreateLoadout();
        using (var tx = Connection.BeginTransaction())
        {
            tx.Retract(loadout.Installation.Id, GameInstallMetadata.Game, GameInstallation.Game.GameId);
            await tx.Commit();
        }

        new LoadoutAcquisition(loadout.LoadoutId).Destination(Connection.Db).IsUnknown.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateAcquisition_GivesTheReplacedItemsGames()
    {
        var first = await AddItem(Destination.Of(GameA));
        var second = await AddItem(Destination.Of(GameB));

        new UpdateAcquisition([first, second]).Destination(Connection.Db)
            .Should().Be(new Destination(new HashSet<GameId> { GameA, GameB }, Shared: false));
    }

    [Fact]
    public async Task UpdateAcquisition_IsSharedWhenAnyReplacedItemIs()
    {
        var shared = await AddItem(new Destination(new HashSet<GameId> { GameA, GameB }, Shared: true));
        var plain = await AddItem(Destination.Of(GameC));

        new UpdateAcquisition([shared, plain]).Destination(Connection.Db)
            .Should().Be(new Destination(new HashSet<GameId> { GameA, GameB, GameC }, Shared: true));
    }

    [Fact]
    public async Task UpdateAcquisition_OfUnassignedItems_IsUnknown()
    {
        var item = await AddItem(Destination.Unknown);

        new UpdateAcquisition([item]).Destination(Connection.Db).IsUnknown.Should().BeTrue();
    }
}
