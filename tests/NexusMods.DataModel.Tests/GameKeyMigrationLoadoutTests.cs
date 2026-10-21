using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Games.RedEngine.Cyberpunk2077;
using NexusMods.Games.TestFramework;
using NexusMods.DataModel.SchemaVersions;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.NexusModsApi;
using Loadout = NexusMods.Sdk.Loadouts.Loadout;

namespace NexusMods.DataModel.Tests;

/// <summary>
/// A loadout written before installs carried a game id keeps working once the data is migrated.
/// </summary>
public class GameKeyMigrationLoadoutTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<GameKeyMigrationLoadoutTests>(helper)
{
    [Fact]
    public async Task MigrateAll_LoadoutOnAPreChangeRow_StillResolvesItsGameAndInstallation()
    {
        var loadout = await SeedPreChangeLoadout(Cyberpunk2077Game.NexusModsGameId.Value);

        await ServiceProvider.GetRequiredService<MigrationService>().MigrateAll();

        Refresh(ref loadout);
        loadout.Game.Should().BeOfType<Cyberpunk2077Game>();
        loadout.InstallationInstance.Should().Be(GameInstallation);
    }

    [Fact]
    public async Task MigrateAll_LoadoutOnAnUnknownGame_IsNotFoundInsteadOfThrowing()
    {
        var loadout = await SeedPreChangeLoadout(NexusModsGameId.From(987654));

        await ServiceProvider.GetRequiredService<MigrationService>().MigrateAll();

        Refresh(ref loadout);
        loadout.TryGetGame(out _).Should().BeFalse();
        GameRegistry.TryGetGameInstallation(loadout, out _).Should().BeFalse();
        GameRegistry.TryGetMetadata(GameInstallation, out var metadata).Should().BeFalse("the unkeyed row is not this game's");
        metadata.IsValid().Should().BeFalse();
    }

    private async Task<Loadout.ReadOnly> SeedPreChangeLoadout(NexusModsGameId nexusId)
    {
        using var tx = Connection.BeginTransaction();
        var metadata = new GameInstallMetadata.New(tx)
        {
            Store = GameInstallation.LocatorResult.Store,
            Path = GameInstallation.Locations[LocationId.Game].Path.ToString(),
            GameId = nexusId,
            Name = GameInstallation.Game.DisplayName,
        };
        var loadout = new Loadout.New(tx)
        {
            Name = "Pre-change loadout",
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
