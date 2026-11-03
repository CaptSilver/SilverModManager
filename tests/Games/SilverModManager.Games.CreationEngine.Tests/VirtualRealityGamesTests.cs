using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Games.CreationEngine.Abstractions;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests;

/// <summary>
/// Which games declare themselves VR titles, and which keep a script extender plugin folder. Resolved from the real
/// service graph.
/// </summary>
public class VirtualRealityGamesTests(IServiceProvider serviceProvider)
{
    private T GameOf<T>() where T : IGame => serviceProvider.GetServices<IGame>().OfType<T>().Single();

    [Fact]
    public void SkyrimVrAndFallout4VrAreVirtualRealityGames()
    {
        GameOf<CreationEngine.SkyrimVR.SkyrimVR>().Should().BeAssignableTo<IVirtualRealityGame>();
        GameOf<CreationEngine.Fallout4VR.Fallout4VR>().Should().BeAssignableTo<IVirtualRealityGame>();
    }

    [Fact]
    public void FlatScreenGamesAreNot()
    {
        GameOf<CreationEngine.SkyrimSE.SkyrimSE>().Should().NotBeAssignableTo<IVirtualRealityGame>();
        GameOf<CreationEngine.Fallout4.Fallout4>().Should().NotBeAssignableTo<IVirtualRealityGame>();
    }

    [Fact]
    public void TheVrGamesLoadScriptExtenderPluginsFromTheirOwnFolders()
    {
        GameOf<CreationEngine.SkyrimVR.SkyrimVR>().ScriptExtenderPluginFolder.Should().Be(new Sdk.Games.GamePath(Sdk.Games.LocationId.Game, "Data/SKSE/Plugins"));
        GameOf<CreationEngine.Fallout4VR.Fallout4VR>().ScriptExtenderPluginFolder.Should().Be(new Sdk.Games.GamePath(Sdk.Games.LocationId.Game, "Data/F4SE/Plugins"));
    }
}
