using FluentAssertions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Games.Unity.Tests.Fixtures;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Unity.Tests;

public class BepInExLoaderTests(ITestOutputHelper outputHelper) : AUnityInstallerTests<BepInExLoaderTests>(outputHelper)
{
    private static readonly string[] PackPaths =
    [
        "BepInExPack/winhttp.dll",
        "BepInExPack/doorstop_config.ini",
        "BepInExPack/BepInEx/core/BepInEx.Preloader.dll",
    ];

    private async Task<(Loadout.ReadOnly Loadout, LoadoutItemGroup.ReadOnly Pack)> LoadoutWithPack()
    {
        var loadout = await CreateLoadout();
        var pack = await Install(PackInstaller, loadout, await AddFromPathsNamed("Test-LoaderPack-6.0.0.zip", PackPaths));
        return (Refresh(loadout), pack);
    }

    [Fact]
    public async Task IsInstalledIsFalseOnAnEmptyLoadout()
    {
        var loadout = await CreateLoadout();

        Game.Loader.IsInstalled(loadout).Should().BeFalse();
    }

    [Fact]
    public async Task IsInstalledIsTrueOnceThePackIsInstalled()
    {
        var (loadout, _) = await LoadoutWithPack();

        Game.Loader.IsInstalled(loadout).Should().BeTrue();
    }

    [Fact]
    public async Task IsInstalledIsFalseWhenThePackIsDisabled()
    {
        var (loadout, pack) = await LoadoutWithPack();
        await DisableItem(pack.Id);

        Game.Loader.IsInstalled(Refresh(loadout)).Should().BeFalse();
    }

    [Fact]
    public async Task TheProxyDllNeedsANativeThenBuiltInOverrideOnlyWhenThePackIsInstalled()
    {
        var empty = await CreateLoadout();
        Game.Loader.RequiredWineDllOverrides(empty).Should().BeEmpty();

        var (loadout, _) = await LoadoutWithPack();
        // Compared by parts: the override holds an ImmutableArray, which a record compares by reference.
        var overrides = Game.Loader.RequiredWineDllOverrides(loadout);
        overrides.Should().ContainSingle().Which.DllName.Should().Be("winhttp");
        overrides.Single().OverrideTypes.Should().Equal(WineDllOverrideType.Native, WineDllOverrideType.BuiltIn);
    }

    [Fact]
    public async Task HasPluginsIsTrueOnlyForAnEnabledFileUnderThePluginsFolder()
    {
        var loadout = await CreateLoadout();
        Game.Loader.HasMods(loadout).Should().BeFalse();

        using (var tx = Connection.BeginTransaction())
        {
            var configGroup = AddEmptyGroup(tx, loadout.LoadoutId, "config");
            AddFile(tx, loadout.LoadoutId, configGroup, new GamePath(LocationId.Game, "BepInEx/config/a.cfg"));
            await tx.Commit();
        }
        Game.Loader.HasMods(Refresh(loadout)).Should().BeFalse();

        EntityId pluginGroup;
        using (var tx = Connection.BeginTransaction())
        {
            var group = AddEmptyGroup(tx, loadout.LoadoutId, "plugin");
            AddFile(tx, loadout.LoadoutId, group, new GamePath(LocationId.Game, "BepInEx/plugins/Zehs-REPOLib/REPOLib.dll"));
            pluginGroup = (await tx.Commit())[group.Value];
        }
        Game.Loader.HasMods(Refresh(loadout)).Should().BeTrue();

        await DisableItem(pluginGroup);
        Game.Loader.HasMods(Refresh(loadout)).Should().BeFalse();
    }

    [Theory]
    [InlineData("Test-LoaderPack-6.0.0.zip", true)]
    [InlineData("other-loaderpack-1.0.0.zip", true)]
    [InlineData("Zehs-REPOLib-4.2.0.zip", false)]
    [InlineData("..zip", false)]
    [InlineData(".zip", false)]
    public void IsLoaderPackNameMatchesThePackageAndItsAliasesIgnoringCase(string itemName, bool expected)
    {
        Game.Loader.IsLoaderPackName(itemName).Should().Be(expected);
    }

    [Theory]
    [InlineData("BepInEx/LogOutput.log", true)]
    [InlineData("BepInEx/cache/chainloader_typeloader.dat", true)]
    [InlineData("BepInEx/cache/sub/file.bin", true)]
    [InlineData("BepInEx/plugins/X/X.dll", false)]
    [InlineData("BepInEx/config/BepInEx.cfg", false)]
    [InlineData("REPO.exe", false)]
    public void TheLoaderNamesWhatBepInExWritesWhileTheGameRuns(string path, bool ignored)
    {
        Game.Loader.IsRuntimeOutput(new GamePath(LocationId.Game, path)).Should().Be(ignored);
    }

    [Fact]
    public void TheFilterOnlyLooksAtTheGameFolder()
    {
        Game.Loader.IsRuntimeOutput(new GamePath(LocationId.Saves, "BepInEx/LogOutput.log")).Should().BeFalse();
    }
}
