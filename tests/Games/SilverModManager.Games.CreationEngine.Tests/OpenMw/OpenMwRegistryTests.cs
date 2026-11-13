using FluentAssertions;
using SilverModManager.Sdk.Games;
using Xunit;
using static SilverModManager.Games.CreationEngine.OpenMw.OpenMwConstants;

namespace SilverModManager.Games.CreationEngine.Tests.OpenMw;

/// <summary>
/// Two OpenMW configs over one Morrowind folder: the registry matches a loadout to an installation by game, store
/// and game folder, so the two stores are what keeps a Flatpak loadout from writing the native config.
/// </summary>
public class OpenMwRegistryTests(ITestOutputHelper helper) : AOpenMwGameTest<OpenMwRegistryTests>(helper)
{
    protected override int[] RootsWithConfig => [0, 1];

    [Fact]
    public async Task TwoRootsOverOneMorrowindFolderGiveTwoMetadataRowsAndEachLoadoutResolvesToItsOwn()
    {
        var installations = GameRegistry.LocateGameInstallations().Where(i => i.Game is CreationEngine.OpenMw.OpenMw).ToList();
        installations.Select(i => i.LocatorResult.Store).Should().BeEquivalentTo([GameStore.OpenMW, GameStore.OpenMWFlatpak]);
        installations.Select(i => i.Locations[LocationId.Game].Path.GetFullPath()).Distinct().Should().ContainSingle();

        var native = await LoadoutManager.CreateLoadout(installations.Single(i => i.LocatorResult.Store == GameStore.OpenMW), "native");
        var flatpak = await LoadoutManager.CreateLoadout(installations.Single(i => i.LocatorResult.Store == GameStore.OpenMWFlatpak), "flatpak");

        native.Installation.Id.Should().NotBe(flatpak.Installation.Id);
        Refresh(native).InstallationInstance.LocatorResult.StoreIdentifier.Should().Be(Roots.NativeConfig.GetFullPath());
        Refresh(flatpak).InstallationInstance.LocatorResult.StoreIdentifier.Should().Be(Roots.FlatpakConfig.GetFullPath());
        Refresh(flatpak).InstallationInstance.Locations[OpenMwConfig].Path.Should().Be(Roots.FlatpakConfig);
    }
}
