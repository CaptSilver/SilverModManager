using FluentAssertions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.Unity.Tests.Fixtures;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Unity.Tests;

public class MelonLoaderLoaderTests(ITestOutputHelper outputHelper) : AMelonLoaderInstallerTests<MelonLoaderLoaderTests>(outputHelper)
{
    [Theory]
    [InlineData("MelonLoader/Latest.log", true)]
    [InlineData("MelonLoader/Logs/x.log", true)]
    [InlineData("MelonLoader/Il2CppAssemblies/a.dll", true)]
    [InlineData("MelonLoader/Dependencies/Il2CppAssemblyGenerator/Cpp2IL/cpp2il_out/x", true)]
    [InlineData("MelonLoader/Dependencies/SupportModules/a.dll", false)]
    [InlineData("UserData/MelonPreferences.cfg", true)]
    [InlineData("UserData/Loader.cfg", true)]
    [InlineData("UserData/Mod.cfg", false)]
    [InlineData("Mods/A.dll", false)]
    public void TheLoaderNamesWhatMelonLoaderWritesWhileTheGameRuns(string path, bool ignored)
    {
        Game.Loader.IsRuntimeOutput(new GamePath(LocationId.Game, path)).Should().Be(ignored);
    }

    [Fact]
    public void TheFilterOnlyLooksAtTheGameFolder()
    {
        Game.Loader.IsRuntimeOutput(new GamePath(LocationId.Saves, "MelonLoader/Latest.log")).Should().BeFalse();
    }

    [Theory]
    [InlineData("Mods/A.dll", true)]
    [InlineData("Plugins/Sub/B.dll", true)]
    [InlineData("Mods/a.cfg", false)]
    [InlineData("UserLibs/C.dll", false)]
    [InlineData("MelonLoader/net6/MelonLoader.dll", false)]
    public void OnlyALibraryBelowModsOrPluginsHooksTheGame(string path, bool hooks)
    {
        Game.Loader.HooksGameCode(new GamePath(LocationId.Game, path)).Should().Be(hooks);
    }

    [Fact]
    public async Task TheProxyIsVersionDllAndModsAreBelowMods()
    {
        Game.Loader.Proxy.Should().Be(new GamePath(LocationId.Game, "version.dll"));

        var loadout = await CreateLoadout();
        Game.Loader.HasMods(loadout).Should().BeFalse();

        using (var tx = Connection.BeginTransaction())
        {
            var group = AddEmptyGroup(tx, loadout.LoadoutId, "mod");
            AddFile(tx, loadout.LoadoutId, group, new GamePath(LocationId.Game, "Mods/A.dll"));
            await tx.Commit();
        }

        Game.Loader.HasMods(Refresh(loadout)).Should().BeTrue();
    }

    [Fact]
    public async Task AnIl2CppInstallNeedsTheDotNet6DesktopRuntime()
    {
        SetRuntime(UnityRuntime.Il2Cpp);
        var loadout = await CreateLoadout();

        Game.Loader.RequiredPrefixComponents(loadout).Should().Equal(PrefixComponents.DotNetDesktop6.Id);
    }

    [Fact]
    public async Task AMonoInstallNeedsNothing()
    {
        SetRuntime(UnityRuntime.Mono);
        var loadout = await CreateLoadout();

        Game.Loader.RequiredPrefixComponents(loadout).Should().BeEmpty();
    }

    [Theory]
    [InlineData("Test-MelonLoader-0.7.1.zip", true)]
    [InlineData("other-melonloader-1.0.0.zip", true)]
    [InlineData("Someone-SomeMod-1.0.0.zip", false)]
    public void IsLoaderPackNameMatchesThePackageAndItsAliasesIgnoringCase(string itemName, bool expected)
    {
        Game.Loader.IsLoaderPackName(itemName).Should().Be(expected);
    }
}
