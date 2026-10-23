using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Overlay;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using NexusMods.Paths;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Games.FromSoft.Tests.Overlay;

public class OverlayLayoutTests(ITestOutputHelper helper) : AEldenRingTest<OverlayLayoutTests>(helper)
{
    private OverlayLayout For(SilverModManager.Sdk.Loadouts.LoadoutId loadout)
        => OverlayLayout.For(ServiceProvider.GetRequiredService<ISettingsManager>().Get<FromSoftSettings>(), FileSystem, GameInstallation, loadout);

    [Fact]
    public async Task TheLowerFolderIsTheRealGameFolder()
    {
        var loadout = await CreateLoadout();

        For(loadout.LoadoutId).Lower.Should().Be(EldenRingPaths.GameFolder(GameInstallation));
    }

    [Fact]
    public async Task TheLayoutIsDeterministicAndTheWorkFolderSitsBesideTheUpperFolder()
    {
        var loadout = await CreateLoadout();

        var layout = For(loadout.LoadoutId);

        layout.Should().Be(For(loadout.LoadoutId));
        layout.Work.Parent.Should().Be(layout.Upper.Parent);
        layout.Upper.FileName.Should().Be("upper");
        layout.Work.FileName.Should().Be("work");
    }

    [Fact]
    public async Task DifferentLoadoutsGetDifferentFolders()
    {
        var first = await CreateLoadout();
        var second = await CreateLoadout();

        var a = For(first.LoadoutId);
        var b = For(second.LoadoutId);

        a.Upper.Should().NotBe(b.Upper);
        a.Work.Should().NotBe(b.Work);
    }

    [Fact]
    public async Task EverythingButTheLowerFolderIsUnderTheDataRootAndTheInstallationsAppId()
    {
        var loadout = await CreateLoadout();
        var dataRoot = EldenRingPaths.Me3Root(ServiceProvider.GetRequiredService<ISettingsManager>().Get<FromSoftSettings>(), FileSystem);

        var layout = For(loadout.LoadoutId);

        layout.Upper.InFolder(dataRoot / "1245620" / "overlay").Should().BeTrue();
        layout.Work.InFolder(dataRoot / "1245620" / "overlay").Should().BeTrue();
        layout.Upper.InFolder(GameInstallation.Locations[SilverModManager.Sdk.Games.LocationId.Game].Path).Should().BeFalse();
    }
}
