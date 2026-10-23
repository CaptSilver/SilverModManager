using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Backend;
using SilverModManager.CrossPlatform;
using SilverModManager.Games.Generic;
using SilverModManager.Games.RedEngine;
using SilverModManager.Games.RedEngine.Cyberpunk2077;
using SilverModManager.StandardGameLocators.TestHelpers;
using Xunit;

namespace SilverModManager.Games.TestFramework;

/// <summary>
/// A override for the <see cref="AIsolatedGameTest{TGame}"/> for the <see cref="Cyberpunk2077Game"/>.
/// </summary>
public class ACyberpunkIsolatedGameTest<TTest>(ITestOutputHelper helper) : AIsolatedGameTest<TTest, Cyberpunk2077Game>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddOSInterop()
            .AddRuntimeDependencies()
            .RemoveRuntimeDependencyChecker()
            .AddGenericGameSupport()
            .AddFakeToolRunner()
            .AddUniversalGameLocator<Cyberpunk2077Game>(new Version("1.61"))
            .AddRedEngineGames();
    }
}
