using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;

namespace SilverModManager.DataModel.Tests;

/// <summary>
/// Install rows written before their game had a Nexus id get it on the next start; the Nexus pages read it from the row.
/// </summary>
public class InstallNexusIdBackfillTests(ITestOutputHelper helper) : AStubbedGameIsolatedTest<InstallNexusIdBackfillTests>(helper)
{
    private InstallNexusIdBackfill Backfill() => new(Connection, ServiceProvider.GetServices<IGameData>(), NullLogger<InstallNexusIdBackfill>.Instance);

    private async Task<GameInstallMetadata.ReadOnly> SeedRowWithoutNexusId(GameInstallation installation, NexusModsGameId? nexusId = null)
    {
        using var tx = Connection.BeginTransaction();
        var row = new GameInstallMetadata.New(tx)
        {
            Store = installation.LocatorResult.Store,
            Path = installation.Locations[LocationId.Game].Path.ToString(),
            Game = installation.Game.GameId,
            Name = installation.Game.DisplayName,
        };
        if (nexusId.HasValue) row.GameId = nexusId.Value;
        var result = await tx.Commit();
        return result.Remap(row);
    }

    private GameInstallMetadata.ReadOnly Reload(GameInstallMetadata.ReadOnly row) => GameInstallMetadata.Load(Connection.Db, row.Id);

    [Fact]
    public async Task Run_RowWithoutNexusId_GetsTheIdOfItsGame()
    {
        var installation = GameRegistry.LocateGameInstallations().First(g => g.Game is StubbedGame);
        var row = await SeedRowWithoutNexusId(installation);

        (await Backfill().Run()).Should().Be(1);

        GameInstallMetadata.GameId.TryGetValue(Reload(row), out var id).Should().BeTrue();
        id.Should().Be(StubbedGame.NexusModsGameId.Value);
    }

    [Fact]
    public async Task Run_CalledTwice_ChangesNothingTheSecondTime()
    {
        var installation = GameRegistry.LocateGameInstallations().First(g => g.Game is StubbedGame);
        await SeedRowWithoutNexusId(installation);
        var backfill = Backfill();

        await backfill.Run();

        (await backfill.Run()).Should().Be(0);
    }

    [Fact]
    public async Task Run_RowOfAnInstallThatIsNotLocated_GetsTheIdOfItsGame()
    {
        // A library that is not mounted at this start: no locator reports the folder the row names.
        using var tx = Connection.BeginTransaction();
        var unlocated = new GameInstallMetadata.New(tx)
        {
            Store = GameStore.Steam,
            Path = "/mnt/unmounted-library/steamapps/common/Stubbed Game",
            Game = StubbedGame.GameId,
            Name = "Stubbed Game",
        };
        var row = (await tx.Commit()).Remap(unlocated);

        (await Backfill().Run()).Should().Be(1);

        GameInstallMetadata.GameId.Get(Reload(row)).Should().Be(StubbedGame.NexusModsGameId.Value);
    }

    [Fact]
    public async Task Run_RowWithADifferentNexusId_KeepsIt()
    {
        var installation = GameRegistry.LocateGameInstallations().First(g => g.Game is StubbedGame);
        var other = NexusModsGameId.From(StubbedGame.NexusModsGameId.Value.Value + 1);
        var row = await SeedRowWithoutNexusId(installation, other);

        (await Backfill().Run()).Should().Be(0);

        GameInstallMetadata.GameId.Get(Reload(row)).Should().Be(other);
    }
}
