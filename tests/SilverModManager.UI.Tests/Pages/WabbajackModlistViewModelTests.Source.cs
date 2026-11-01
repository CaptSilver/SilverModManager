using System.Reactive.Linq;
using FluentAssertions;
using SilverModManager.App.UI.Pages.Collections;
using SilverModManager.App.UI.Pages.Wabbajack;
using SilverModManager.Sdk.Library;
using SilverModManager.UI.Tests.Pages.Collections;

namespace SilverModManager.UI.Tests.Pages;

public partial class WabbajackModlistViewModelTests
{
    [Fact]
    public async Task ASourceWithoutALoadoutFollowsTheLoadoutThePickerHolds()
    {
        var (vm, _, _) = await Open(Plan());
        var picked = vm.SelectedLoadout!.Id!.Value;
        var file = await CollectionSeed.AddModlistFile(Connection, $"Picker list {Guid.NewGuid():N}");
        var group = await WabbajackGroupSeed.AddGroup(Connection, file.Id, picked, file.Name);
        await WabbajackGroupSeed.Finish(Connection, group);
        var services = new OverridingServices(Provider, typeof(IWabbajackModlistViewModel), vm);
        var source = new WabbajackModlistSource(services, loadoutId: null, file);
        source.CreateDownloads().Should().BeSameAs(vm);

        (await source.Group.FirstAsync()).Value.Value.Should().Be(group, "the picker starts on the first loadout of the game, which holds the install");
        (await source.IsInstalled.FirstAsync()).Should().BeTrue();

        await vm.SelectLoadoutAsync(WabbajackLoadoutChoice.CreateNew, CancellationToken.None);
        (await source.Group.FirstAsync()).HasValue.Should().BeFalse("a loadout that does not exist yet holds no install");
        (await source.StateLine.FirstAsync()).Should().Be("Not installed yet");
        (await source.CanToggle.FirstAsync()).Should().BeFalse();

        await vm.SelectLoadoutAsync(vm.LoadoutChoices.First(choice => choice.Id == picked), CancellationToken.None);
        (await source.Group.FirstAsync()).Value.Value.Should().Be(group);
    }
}
