using System.Collections.Immutable;
using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.IO;
using NexusMods.Sdk.Loadouts;
using NexusMods.StandardGameLocators.TestHelpers;
using NexusMods.StandardGameLocators.TestHelpers.StubbedGames;
using Loadout = NexusMods.Sdk.Loadouts.Loadout;

namespace NexusMods.DataModel.Tests;

/// <summary>
/// Two installs of one game, and two games that share a Nexus id: installations and loadouts have to
/// tell them apart by the game's own id.
/// </summary>
public class GameKeyedInstallationTests(ITestOutputHelper helper) : AStubbedGameIsolatedTest<GameKeyedInstallationTests>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        // Registered first, so a lookup by Nexus id alone finds the wrong game.
        services.AddGame<SiblingStubbedGame>();

        return base.AddServices(services)
            .AddUniversalGameLocator<StubbedGame>(Version.Parse("0.0.0"));
    }

    private GameInstallation[] Installs() => GameRegistry.LocateGameInstallations().Where(g => g.Game is StubbedGame).ToArray();

    [Fact]
    public async Task ManageInstallation_GameWithNexusId_WritesGameAndNexusId()
    {
        var metadata = await LoadoutManager.ManageInstallation(Installs()[0]);

        metadata.Game.Value.Should().Be(StubbedGame.GameId);
        GameInstallMetadata.GameId.TryGetValue(metadata, out var nexusId).Should().BeTrue();
        nexusId.Should().Be(StubbedGame.NexusModsGameId.Value);
    }

    [Fact]
    public async Task TryGetMetadata_TwoInstallsOfOneGameAndStore_GetTheirOwnRows()
    {
        var installs = Installs();
        installs.Should().HaveCount(2);
        installs[0].LocatorResult.Store.Should().Be(installs[1].LocatorResult.Store);

        var first = await LoadoutManager.ManageInstallation(installs[0]);
        var second = await LoadoutManager.ManageInstallation(installs[1]);

        second.Id.Should().NotBe(first.Id, "a second folder is a second install, not the first one again");
        (await LoadoutManager.ManageInstallation(installs[0])).Id.Should().Be(first.Id);
        (await LoadoutManager.ManageInstallation(installs[1])).Id.Should().Be(second.Id);

        GameRegistry.TryGetMetadata(installs[0], out var foundFirst).Should().BeTrue();
        foundFirst.Id.Should().Be(first.Id);
        GameRegistry.TryGetMetadata(installs[1], out var foundSecond).Should().BeTrue();
        foundSecond.Id.Should().Be(second.Id);
    }

    [Fact]
    public async Task TryGetGameInstallation_TwoInstallsOfOneGameAndStore_ResolvesEachLoadoutToItsInstall()
    {
        var installs = Installs();
        var first = await SeedLoadout(await LoadoutManager.ManageInstallation(installs[0]));
        var second = await SeedLoadout(await LoadoutManager.ManageInstallation(installs[1]));

        GameRegistry.TryGetGameInstallation(first, out var foundFirst).Should().BeTrue();
        foundFirst.Should().Be(installs[0]);
        GameRegistry.TryGetGameInstallation(second, out var foundSecond).Should().BeTrue();
        foundSecond.Should().Be(installs[1]);
    }

    [Fact]
    public async Task LoadoutGame_TwoGamesShareANexusId_ResolvesByGameId()
    {
        var loadout = await SeedLoadout(await LoadoutManager.ManageInstallation(Installs()[0]));

        loadout.Game.Should().BeOfType<StubbedGame>();
    }

    private async Task<Loadout.ReadOnly> SeedLoadout(GameInstallMetadata.ReadOnly metadata)
    {
        using var tx = Connection.BeginTransaction();
        var loadout = new Loadout.New(tx)
        {
            Name = Guid.NewGuid().ToString(),
            ShortName = "ZZZ",
            InstallationId = metadata,
            LoadoutKind = LoadoutKind.Default,
            Revision = 0,
            GameVersion = VanityVersion.From("Unknown"),
        };
        var result = await tx.Commit();
        return result.Remap(loadout);
    }
}

/// <summary>
/// A game without a Nexus id has nothing but its own id to be found by.
/// </summary>
public class NoNexusIdInstallationTests(ITestOutputHelper helper) : AIsolatedGameTest<NoNexusIdInstallationTests, NoNexusIdStubbedGame>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddGame<NoNexusIdStubbedGame>()
            .AddUniversalGameLocator<NoNexusIdStubbedGame>(Version.Parse("0.0.0"));
    }

    [Fact]
    public async Task ManageInstallation_GameWithoutNexusId_WritesGameAndNoNexusId()
    {
        var metadata = await LoadoutManager.ManageInstallation(GameInstallation);

        metadata.Game.Value.Should().Be(NoNexusIdStubbedGame.GameId);
        metadata.Contains(GameInstallMetadata.GameId).Should().BeFalse();
        GameRegistry.TryGetMetadata(GameInstallation, out var found).Should().BeTrue();
        found.Id.Should().Be(metadata.Id);
    }

    [Fact]
    public async Task TryGetMetadata_InstallMovedToAnotherPath_StillFindsTheRow()
    {
        var metadata = await LoadoutManager.ManageInstallation(GameInstallation);

        // Steam moved the game to another library: same game, same store, different folder.
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(metadata.Id, GameInstallMetadata.Path, "/mnt/other-library/game");
            await tx.Commit();
        }

        GameRegistry.TryGetMetadata(GameInstallation, out var found).Should().BeTrue();
        found.Id.Should().Be(metadata.Id);
    }

    [Fact]
    public async Task LoadoutGame_ResolvesWithoutNexusId()
    {
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, "No Nexus Id");

        loadout.Game.Should().BeOfType<NoNexusIdStubbedGame>();
        loadout.InstallationInstance.Should().Be(GameInstallation);
    }
}
