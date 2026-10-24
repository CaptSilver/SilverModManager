using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Games.Versions;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.StardewValley.Tests;

/// <summary>
/// Own service graph with a Stardew-shaped <c>deps.json</c>, so the shared fixture (whose empty deps.json keeps
/// every emitter snapshot on the loadout's game version) stays as it is.
/// </summary>
public class LauncherVersionTests(ITestOutputHelper helper) : AIsolatedGameTest<LauncherVersionTests, StardewValley>(helper)
{
    private const string DepsJson = """
        {
          "runtimeTarget": { "name": ".NETCoreApp,Version=v6.0", "signature": "" },
          "targets": { ".NETCoreApp,Version=v6.0": { "Stardew Valley/1.6.15": {} } },
          "libraries": { "Stardew Valley/1.6.15": { "type": "project", "serviceable": false, "sha512": "" } }
        }
        """;

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        var gameFiles = new Dictionary<RelativePath, byte[]>
        {
            { "Stardew Valley.deps.json", Encoding.UTF8.GetBytes(DepsJson) },
        };

        return base.AddServices(services)
            .AddStardewValley()
            .AddUniversalGameLocator<StardewValley>(new Version(1, 0), gameFiles);
    }

    [Fact]
    public void GetLocalVersion_FallsBackToDepsJson_WhenTheDllIsAbsent()
    {
        var launcherVersion = Game.ReadLauncherVersion(GameInstallation);

        launcherVersion.Source.Should().Be(LauncherVersionSource.DepsJson);
        launcherVersion.Version.Value.Should().Be(new Version(1, 6, 15));
        Game.GetLocalVersion(GameInstallation).Value.Should().Be(new Version(1, 6, 15));
    }
}
