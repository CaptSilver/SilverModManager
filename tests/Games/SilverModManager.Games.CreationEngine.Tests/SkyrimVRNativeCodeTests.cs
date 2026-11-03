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
}
