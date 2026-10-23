using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.NexusWebApi;
using NexusMods.Games.TestFramework;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.StandardGameLocators.TestHelpers;
using NexusMods.StandardGameLocators.TestHelpers.StubbedGames;
using NSubstitute;
using Xunit;

namespace NexusMods.Collections.Tests;

/// <summary>
/// A collection is filed under a Nexus game domain, so a loadout of a game that has no Nexus id cannot be exported.
/// </summary>
public class CollectionExportTests(ITestOutputHelper helper) : AIsolatedGameTest<CollectionExportTests, NoNexusIdStubbedGame>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddGame<NoNexusIdStubbedGame>()
            .AddUniversalGameLocator<NoNexusIdStubbedGame>(Version.Parse("0.0.0"));
    }

    [Fact]
    public async Task LoadoutItemGroupToCollectionManifest_GameWithoutNexusId_RefusesWithAReadableMessage()
    {
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, "No Nexus Id");
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, "Collection");
        var result = await tx.Commit();
        var groupEntity = LoadoutItemGroup.Load(result.Db, result[group.Value]);

        var act = () => CollectionCreator.LoadoutItemGroupToCollectionManifest(
            groupEntity,
            Substitute.For<IGameDomainToGameIdMappingCache>(),
            default,
            VanityVersion.From("1.0.0"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*has no Nexus Mods id*");
    }
}
