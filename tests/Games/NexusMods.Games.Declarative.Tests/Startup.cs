using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.GuidedInstallers;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Backend;
using NexusMods.Games.CreationEngine;
using NexusMods.Games.Declarative;
using NexusMods.Games.Declarative.Catalog;
using NexusMods.Games.FromSoft;
using NexusMods.Games.Larian.BaldursGate3;
using NexusMods.Games.MountAndBlade2Bannerlord;
using NexusMods.Games.RedEngine;
using NexusMods.Games.StardewValley;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.StandardGameLocators.TestHelpers;

namespace NexusMods.Games.Declarative.Tests;

public class Startup
{
    public void ConfigureServices(IServiceCollection container)
    {
        container
            .AddSingleton<IGuidedInstaller, NullGuidedInstaller>()
            .AddDefaultServicesForTesting()
            .AddUniversalGameLocator<CreationEngine.SkyrimSE.SkyrimSE>(new Version("1.6.1170"))
            .AddRedEngineGames()
            .AddStardewValley()
            .AddBaldursGate3()
            .AddCreationEngine()
            .AddMountAndBlade2Bannerlord()
            .AddFromSoftGames()
            .AddDeclarativeGames(new InMemoryFileSystem(OSInformation.FakeUnix), additionalBundled: [new DefinitionSource("test/harness.yaml", Samples.Harness), RoundTripDefinitions.Fixture])
            .AddUniversalGameLocator(GameId.From(Samples.HarnessId), stores: [GameStore.Unknown])
            .AddRoundTripLocators()
            .AddLogging(builder => builder.AddXUnit())
            .AddGames()
            .AddGameServices()
            .AddLoadoutAbstractions()
            .Validate();
    }
}

internal static class RoundTripLocators
{
    /// <summary>A stubbed locator for every definition the round-trip theory covers, with a few vanilla files written.</summary>
    public static IServiceCollection AddRoundTripLocators(this IServiceCollection services)
    {
        // One install per plan, each in its own folder, so a case never sees what another left behind.
        foreach (var definition in RoundTripDefinitions.All())
        {
            var gameId = GameId.From(definition.Id);
            foreach (var plan in RoundTripDefinitions.Plans)
            {
                var folderName = RoundTripDefinitions.FolderName(definition.Id, plan);
                services.AddSingleton<IGameLocator>(s => new UniversalStubbedGameLocator(
                    s.GetServices<IGame>().Single(game => game.GameId == gameId),
                    folderName,
                    s.GetRequiredService<IFileSystem>(),
                    s.GetRequiredService<TemporaryFileManager>(),
                    RoundTripDefinitions.VanillaFiles(definition)));
            }
        }
        return services;
    }
}
