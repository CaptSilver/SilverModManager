using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Sdk.Games;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests;

/// <summary>Which Skyrim VR files count as code that hooks the game, resolved from the real service graph.</summary>
public class SkyrimVRNativeCodeTests(IServiceProvider serviceProvider)
{
    [Theory]
    [InlineData("sksevr_loader.exe", true)]
    [InlineData("sksevr_1_4_15.dll", true)]
    [InlineData("Data/SKSE/Plugins/SkyrimUpscaler.dll", true)]
    [InlineData("SkyrimVR.exe", false)]
    [InlineData("Data/SKSE/Plugins/x.ini", false)]
    [InlineData("mysksevr_x.dll", false)]
    public void HooksGameCode_NamesTheLoaderAndPluginDlls(string path, bool hooks)
    {
        var game = serviceProvider.GetServices<IGame>().OfType<CreationEngine.SkyrimVR.SkyrimVR>().Single();

        ((IGameNativeCode)game).HooksGameCode(new GamePath(LocationId.Game, path)).Should().Be(hooks);
    }

    [Theory]
    [InlineData("sksevr_loader.exe", NativeCodeRole.LoaderRuntime)]
    [InlineData("sksevr_1_4_15.dll", NativeCodeRole.LoaderRuntime)]
    [InlineData("Data/SKSE/Plugins/SkyrimUpscaler.dll", NativeCodeRole.LoaderPlugin)]
    [InlineData("SkyrimVR.exe", null)]
    [InlineData("Data/SKSE/Plugins/x.ini", null)]
    public void RoleOf_SplitsTheRuntimeFromItsPlugins(string path, NativeCodeRole? role)
    {
        var game = serviceProvider.GetServices<IGame>().OfType<CreationEngine.SkyrimVR.SkyrimVR>().Single();

        ((IGameNativeCode)game).RoleOf(new GamePath(LocationId.Game, path)).Should().Be(role);
    }

    [Theory]
    [InlineData("sksevr_loader.exe")]
    [InlineData("sksevr_1_4_15.dll")]
    [InlineData("Data/SKSE/Plugins/SkyrimUpscaler.dll")]
    public void EveryHookedPathHasARole(string path)
    {
        var game = (IGameNativeCode)serviceProvider.GetServices<IGame>().OfType<CreationEngine.SkyrimVR.SkyrimVR>().Single();
        var gamePath = new GamePath(LocationId.Game, path);

        game.HooksGameCode(gamePath).Should().BeTrue();
        game.RoleOf(gamePath).Should().NotBeNull();
    }
}
