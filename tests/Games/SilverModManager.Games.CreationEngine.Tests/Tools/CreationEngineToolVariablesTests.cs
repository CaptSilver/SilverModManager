using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games.Tools;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Games;
using SilverModManager.StandardGameLocators.TestHelpers;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests.Tools;

/// <summary>
/// xEdit under Proton cannot find Data, the ini or the plugin list on its own, so a Creation Engine game hands the
/// tool runner those paths. <c>-I:</c> takes the ini file itself, not its folder (xEdit's wbInit reads it as the
/// game ini file name).
/// </summary>
public class CreationEngineToolVariablesTests(ITestOutputHelper helper)
    : AIsolatedGameTest<CreationEngineToolVariablesTests, CreationEngine.SkyrimSE.SkyrimSE>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddCreationEngine()
            .AddUniversalGameLocator<CreationEngine.SkyrimSE.SkyrimSE>(new Version("1.6.1170"), locationsFileSystem: IsolatedKnownFolders.Create);
    }

    [Fact]
    public async Task Skyrim_SuppliesDataPluginsAndIni()
    {
        var loadout = await CreateLoadout();

        var variables = ((IToolVariableSource)Game).ToolVariables(loadout);

        variables[ToolVariableNames.DataFolder].Should().Be(Game.DataPath);
        variables[ToolVariableNames.PluginsFile].Should().Be(Game.PluginsFile);
        variables[ToolVariableNames.IniFile].Should().Be(new GamePath(LocationId.Preferences, "Skyrim.ini"));
    }
}
