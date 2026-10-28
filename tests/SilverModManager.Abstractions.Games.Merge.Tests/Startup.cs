using SilverModManager.Sdk.Hashes;
using NexusMods.Hashing.xxHash3;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SilverModManager.Sdk.Games;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Backend;
using SilverModManager.Games.TestFramework;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;

// Every game test creates its loadout through a real synchronize over the one stub game folder, and the engine tests
// write and replace files in it; run side by side they trip each other (a vanilla file vanishing mid-merge, a
// database assertion landing in whichever test happens to be running).
[assembly: CollectionBehavior(DisableTestParallelization = true)]

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
            .AddGame<StubbedGame>()
            .AddUniversalGameLocator<StubbedGame>(Version.Parse("0.0.0"));

        // The default graph holds the baseline trust; these tests answer for it.
        services.Replace(ServiceDescriptor.Singleton<IVanillaTrust, AlwaysTrusted>());
    }

    /// <summary>Resolves the engine in the service graph; tests that care about trust build their own engine with their own answer.</summary>
    private sealed class AlwaysTrusted : IVanillaTrust
    {
        public bool IsTrusted(GameInstallation installation, GamePath path, MultiHash currentHashes, out string reason)
        {
            reason = "";
            return true;
        }
    }
}
