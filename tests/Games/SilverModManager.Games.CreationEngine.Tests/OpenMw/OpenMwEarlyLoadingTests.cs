using FluentAssertions;
using NexusMods.Paths;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests.OpenMw;

/// <summary>
/// <c>builtin.omwscripts</c> and the game's own masters load before anything the app orders. A config the user's
/// openmw.cfg chains to with <c>config=</c> loads after it, so its content is not early, and neither are the user's
/// own plugins or anything in data-local.
/// </summary>
public class OpenMwEarlyLoadingTests(ITestOutputHelper helper) : AOpenMwGameTest<OpenMwEarlyLoadingTests>(helper)
{
    protected override string ConfigText => "config=chain\n" + Install.Config("content=Own.esp\ncontent=Local.esp\n");

    protected override string[] PluginsInMorrowind => ["Own.esp"];

    protected override string[] PluginsInDataLocal => ["Local.esp"];

    [Fact]
    public void BuiltInScriptsAndTheMastersLoadEarly() =>
        Game.EarlyLoadingPlugins(GameInstallation).Should().Equal("builtin.omwscripts", "Morrowind.esm", "Tribunal.esm", "Bloodmoon.esm");

    [Fact]
    public async Task ChainedConfigContentIsNotEarly()
    {
        var directory = Roots.NativeConfig / "chain";
        directory.CreateDirectory();
        await (directory / "openmw.cfg").WriteAllTextAsync("content=Later.esm\ncontent=LaterAddon.omwaddon\n");

        Game.EarlyLoadingPlugins(GameInstallation).Should().NotContain(["Later.esm", "LaterAddon.omwaddon", "Own.esp", "Local.esp"]);
    }

    [Fact]
    public void OnlyThePluginsSomeDirectoryHasCountAsInstalled() =>
        Game.InstalledEarlyLoadingPlugins(GameInstallation).Should().Equal("Morrowind.esm", "Tribunal.esm", "Bloodmoon.esm");
}
