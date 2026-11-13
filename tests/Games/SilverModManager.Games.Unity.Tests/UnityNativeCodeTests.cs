using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Games.Unity.Tests.Fixtures;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Unity.Tests;

public class UnityNativeCodeTests(ITestOutputHelper outputHelper) : AUnityInstallerTests<UnityNativeCodeTests>(outputHelper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
        => base.AddServices(services).AddUnityGames();

    // The loader layout is the same for every row of the table, so the first one stands for all.
    private IGameNativeCode Native => ServiceProvider.GetServices<IGame>().OfType<UnityGame>().First();

    [Theory]
    [InlineData("winhttp.dll", NativeCodeRole.LoaderRuntime)]
    [InlineData("BepInEx/core/0Harmony.dll", NativeCodeRole.LoaderRuntime)]
    [InlineData("BepInEx/plugins/Mod.dll", NativeCodeRole.LoaderPlugin)]
    [InlineData("BepInEx/plugins/Sub/Mod.dll", NativeCodeRole.LoaderPlugin)]
    [InlineData("BepInEx/config/x.cfg", null)]
    [InlineData("Sub/winhttp.dll", null)]
    public void RoleOf_SplitsTheRuntimeFromItsPlugins(string path, NativeCodeRole? role)
    {
        Native.RoleOf(new GamePath(LocationId.Game, path)).Should().Be(role);
    }

    [Fact]
    public void HooksGameCodeIsUnchanged()
    {
        Native.HooksGameCode(new GamePath(LocationId.Game, "winhttp.dll")).Should().BeFalse();
        Native.HooksGameCode(new GamePath(LocationId.Game, "BepInEx/plugins/Mod.dll")).Should().BeTrue();
    }
}
