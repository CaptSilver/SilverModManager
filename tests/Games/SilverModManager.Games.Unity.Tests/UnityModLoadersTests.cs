using FluentAssertions;
using SilverModManager.Games.Unity.BepInEx;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Unity.Tests;

public class UnityModLoadersTests
{
    [Fact]
    public void ABepInExRowGetsABepInExLoader()
    {
        var loader = UnityModLoaders.Create(UnityGames.Repo);

        loader.Should().BeOfType<BepInExLoader>();
        loader.Proxy.Should().Be(new GamePath(LocationId.Game, "winhttp.dll"));
        loader.DisplayName.Should().Be("BepInEx");
        loader.LoaderPackage.Should().Be("BepInEx-BepInExPack");
    }
}
