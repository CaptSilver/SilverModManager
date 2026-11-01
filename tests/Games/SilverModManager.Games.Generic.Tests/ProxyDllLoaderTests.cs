using FluentAssertions;
using SilverModManager.Games.Generic.Loaders;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using Xunit;

namespace SilverModManager.Games.Generic.Tests;

/// <summary>What a proxy-DLL loader derives from the two constants its game gives it.</summary>
public class ProxyDllLoaderTests
{
    private sealed class TestLoader(string proxyFile, string modsPrefix) : AProxyDllLoader(proxyFile, modsPrefix);

    [Theory]
    [InlineData("winhttp.dll", "winhttp")]
    [InlineData("dinput8.dll", "dinput8")]
    public void TheWineOverrideNamesTheProxyWithoutItsExtension(string proxyFile, string wineName)
    {
        new TestLoader(proxyFile, "Mods").WineDllName.Should().Be(wineName);
    }

    [Fact]
    public void TheProxySitsNextToTheGameExecutable()
    {
        new TestLoader("dinput8.dll", "reframework").Proxy.Should().Be(new GamePath(LocationId.Game, "dinput8.dll"));
    }
}
