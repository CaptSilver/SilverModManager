using Avalonia.Threading;
using FluentAssertions;
using SilverModManager.App.UI.Pages.LoadoutPage;
using SilverModManager.App.UI.Pages.Wabbajack;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Tests.Pages.Collections;

namespace SilverModManager.UI.Tests.Pages;

public class WabbajackModlistPageFactoryTests(IServiceProvider provider) : AUiTest(provider)
{
    private WabbajackModlistPageFactory Factory => new(Provider);

    [Fact]
    public void AListRemovedFromTheLibraryIsGone()
    {
        var context = new WabbajackModlistPageContext { LibraryFileId = LibraryFileId.From(EntityId.From(0x200000000FFFFFF)) };

        var create = () => Factory.CreateViewModel(context);

        create.Should().Throw<PageTargetGoneException>();
    }

    [Fact]
    public async Task APinnedLoadoutThatWasRemovedIsGone()
    {
        var file = await CollectionSeed.AddModlistFile(Connection, $"Gone loadout {Guid.NewGuid():N}");
        var context = new WabbajackModlistPageContext
        {
            LibraryFileId = LibraryFileId.From(file.Id),
            TargetLoadout = LoadoutId.From(EntityId.From(0x200000000FFFFFE)),
        };

        var create = () => Factory.CreateViewModel(context);

        create.Should().Throw<PageTargetGoneException>();
    }

    [Fact]
    public async Task APageWithoutALoadoutIsTheCollectionPageOfTheListWithAPicker()
    {
        var file = await CollectionSeed.AddModlistFile(Connection, $"Open from library {Guid.NewGuid():N}");

        var page = await Dispatcher.UIThread.InvokeAsync(() =>
            Factory.CreateViewModel(new WabbajackModlistPageContext { LibraryFileId = LibraryFileId.From(file.Id) }));

        page.Name.Should().Be(file.Name);
        page.Downloads.Should().BeAssignableTo<IWabbajackModlistViewModel>().Which.IsLoadoutPinned.Should().BeFalse();
    }

    [Fact]
    public async Task APageForALoadoutPinsTheDownloadsToIt()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"Pinned page {Guid.NewGuid()}");
        var file = await CollectionSeed.AddModlistFile(Connection, $"Pinned list {Guid.NewGuid():N}");

        var page = await Dispatcher.UIThread.InvokeAsync(() =>
            Factory.CreateViewModel(new WabbajackModlistPageContext { LibraryFileId = LibraryFileId.From(file.Id), TargetLoadout = loadout.LoadoutId }));

        page.Downloads.Should().BeAssignableTo<IWabbajackModlistViewModel>().Which.PinnedLoadout.Should().Be(loadout.LoadoutId);
    }
}
