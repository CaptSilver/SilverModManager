using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Sdk.Games;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests;

/// <summary>
/// Script extender plugin DLLs count as code that hooks the game for every game with an extender, not only the
/// ones built on a newer engine. Resolved from the real service graph.
/// </summary>
public class ScriptExtenderPluginFolderCodeTests(IServiceProvider serviceProvider)
{
    [Theory]
    [InlineData(typeof(CreationEngine.FalloutNewVegas.FalloutNewVegas), "Data/NVSE/Plugins/jip_nvse.dll", true)]
    [InlineData(typeof(CreationEngine.FalloutNewVegas.FalloutNewVegas), "Data/NVSE/Plugins/jip_nvse.ini", false)]
    [InlineData(typeof(CreationEngine.FalloutNewVegas.FalloutNewVegas), "Data/Plugins/jip_nvse.dll", false)]
    [InlineData(typeof(CreationEngine.Oblivion.Oblivion), "Data/OBSE/Plugins/MenuQue.dll", true)]
    [InlineData(typeof(CreationEngine.Fallout3.Fallout3), "Data/FOSE/Plugins/x.dll", true)]
    [InlineData(typeof(CreationEngine.SkyrimLE.SkyrimLE), "Data/SKSE/Plugins/x.dll", true)]
    [InlineData(typeof(CreationEngine.Enderal.Enderal), "Data/SKSE/Plugins/x.dll", true)]
    [InlineData(typeof(CreationEngine.EnderalSE.EnderalSE), "Data/SKSE/Plugins/x.dll", true)]
    public void HooksGameCode_CoversThePluginFolder(Type gameType, string path, bool hooks)
    {
        var game = serviceProvider.GetServices<IGame>().Single(g => g.GetType() == gameType);

        ((IGameNativeCode)game).HooksGameCode(new GamePath(LocationId.Game, path)).Should().Be(hooks);
    }

    [Theory]
    [InlineData("OblivionRemastered/Binaries/Win64/dxgi.dll", true)]
    [InlineData("OblivionRemastered/Binaries/Win64/DINPUT8.dll", true)]
    [InlineData("dxgi.dll", true)]
    [InlineData("OblivionRemastered/Content/dxgi.dll", false)]
    public void OblivionRemastered_ProxyDllsBesideTheEngineBinaryAreMods(string path, bool isMod)
    {
        var game = serviceProvider.GetServices<IGame>().OfType<CreationEngine.OblivionRemastered.OblivionRemastered>().Single();

        ModLoaderPaths.LooksLikeAMod(game, default!, new GamePath(LocationId.Game, path)).Should().Be(isMod);
    }
}
