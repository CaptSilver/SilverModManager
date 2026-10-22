using FluentAssertions;
using NexusMods.Abstractions.Loadouts;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Loadouts;
using NexusMods.Games.Unity.Tests.Fixtures;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.Unity.Tests;

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
        Game.Loader.HasPlugins(loadout).Should().BeFalse();

        using (var tx = Connection.BeginTransaction())
        {
            var configGroup = AddEmptyGroup(tx, loadout.LoadoutId, "config");
            AddFile(tx, loadout.LoadoutId, configGroup, new GamePath(LocationId.Game, "BepInEx/config/a.cfg"));
            await tx.Commit();
        }
        Game.Loader.HasPlugins(Refresh(loadout)).Should().BeFalse();

        EntityId pluginGroup;
        using (var tx = Connection.BeginTransaction())
        {
            var group = AddEmptyGroup(tx, loadout.LoadoutId, "plugin");
            AddFile(tx, loadout.LoadoutId, group, new GamePath(LocationId.Game, "BepInEx/plugins/Zehs-REPOLib/REPOLib.dll"));
            pluginGroup = (await tx.Commit())[group.Value];
        }
        Game.Loader.HasPlugins(Refresh(loadout)).Should().BeTrue();

        await DisableItem(pluginGroup);
        Game.Loader.HasPlugins(Refresh(loadout)).Should().BeFalse();
    }

    [Theory]
    [InlineData("Test-LoaderPack-6.0.0.zip", true)]
    [InlineData("other-loaderpack-1.0.0.zip", true)]
    [InlineData("Zehs-REPOLib-4.2.0.zip", false)]
    public void IsLoaderPackNameMatchesThePackageAndItsAliasesIgnoringCase(string itemName, bool expected)
    {
        Game.Loader.IsLoaderPackName(itemName).Should().Be(expected);
    }
}
