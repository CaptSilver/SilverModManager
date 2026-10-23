using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Games.FromSoft.Tests.Fixtures;

public static class FromSoftTestServices
{
    /// <summary>
    /// Registers the FromSoft games with a data root of their own. The default root is one folder under
    /// XDG_DATA_HOME for every test host in the run, so parallel tests would share one Me3Mods folder and a sync in
    /// one test would back up files that another test is deleting.
    /// </summary>
    public static IServiceCollection AddFromSoftGamesWithOwnDataRoot(this IServiceCollection services)
    {
        var dataRoot = new ConfigurablePath(KnownPath.EntryDirectory, $"FromSoft.{Guid.NewGuid():N}");
        return services
            .AddFromSoftGames()
            .OverrideSettingsForTests<FromSoftSettings>(settings => settings with { DataRoot = dataRoot });
    }

    /// <summary>
    /// Registers the FromSoft base and Test Souls, a game that is registered nowhere else, with a data root of its own.
    /// The game has no locator beside the Steam one the fixture installs it for, and <c>AddGame</c> registers no launch
    /// tool, so the tool is added here.
    /// </summary>
    public static IServiceCollection AddTestSouls(this IServiceCollection services)
    {
        var dataRoot = new ConfigurablePath(KnownPath.EntryDirectory, $"FromSoft.{Guid.NewGuid():N}");
        return services
            .AddFromSoftBase()
            .AddGame<TestSoulsGame>()
            .AddSingleton<ITool, RunFromSoftGame<TestSoulsGame>>()
            .OverrideSettingsForTests<FromSoftSettings>(settings => settings with { DataRoot = dataRoot });
    }
}
