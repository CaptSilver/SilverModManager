using NexusMods.Hashing.xxHash3;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Sdk.Games;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Backend;
using SilverModManager.Games.TestFramework;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;

namespace SilverModManager.Abstractions.Games.Merge.Tests;

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddDefaultServicesForTesting()
            .AddGameServices()
            .AddLoadoutAbstractions()
            .AddGames()
            .AddRecordingDeploymentBackend()
            .AddMergeEngine()
            .AddTextMergeEngine()
            .AddSingleton<IVanillaTrust, AlwaysTrusted>()
            .AddGame<StubbedGame>()
            .AddUniversalGameLocator<StubbedGame>(Version.Parse("0.0.0"));
    }

    /// <summary>Resolves the engine in the service graph; tests that care about trust build their own engine with their own answer.</summary>
    private sealed class AlwaysTrusted : IVanillaTrust
    {
        public bool IsTrusted(GameInstallation installation, GamePath path, Hash currentHash, out string reason)
        {
            reason = "";
            return true;
        }
    }
}
