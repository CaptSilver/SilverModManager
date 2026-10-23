using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Backend;
using SilverModManager.Games.TestFramework;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.StardewValley.Tests;

public class Startup
{
    public void ConfigureServices(IServiceCollection container)
    {
        var gameFiles = new Dictionary<RelativePath, byte[]>
        {
            { "Stardew Valley.deps.json", "{}"u8.ToArray() }
        };

        container
            .AddDefaultServicesForTesting()
            .AddUniversalGameLocator<StardewValley>(new Version(1, 0), gameFiles)
            .AddStardewValley()
            .AddLogging(builder => builder.AddXUnit())
            .AddGames()
            .AddGameServices()
            .AddLoadoutAbstractions()
            .Validate();
    }
}
