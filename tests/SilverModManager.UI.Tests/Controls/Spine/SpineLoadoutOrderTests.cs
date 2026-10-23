using System.Collections.Immutable;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Controls.Spine;
using SilverModManager.App.UI.Controls.Spine.Buttons.Download;
using SilverModManager.App.UI.Controls.Spine.Buttons.Icon;
using SilverModManager.App.UI.Controls.Spine.Buttons.Image;
using SilverModManager.App.UI.Settings;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceAttachments;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Settings;
using NSubstitute;
using ReactiveUI;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests.Controls.Spine;

public class SpineLoadoutOrderTests(IServiceProvider provider) : AUiTest(provider)
{
    private static LoadoutId Id(ulong value) => LoadoutId.From(EntityId.From(value));

    private Task<(SpineViewModel Spine, IDisposable Activation)> CreateSpine() =>
        Dispatcher.UIThread.InvokeAsync(CreateSpineOnUi).GetTask();

    private (SpineViewModel Spine, IDisposable Activation) CreateSpineOnUi()
    {
        var windows = Substitute.For<IWindowManager>();
        var controller = Substitute.For<IWorkspaceController>();
        controller.AllWorkspaces.Returns(new ReadOnlyObservableCollection<IWorkspaceViewModel>([]));
        windows.ActiveWorkspaceController.Returns(controller);

        var spine = new SpineViewModel(
            Provider,
            Provider.GetRequiredService<ILogger<SpineViewModel>>(),
            Connection,
            windows,
            new IconButtonViewModel(),
            new IconButtonViewModel(),
            Substitute.For<ISpineDownloadButtonViewModel>(),
            Substitute.For<IWorkspaceAttachmentsFactoryManager>());

        return (spine, spine.Activator.Activate());
    }

    /// <summary>
    /// The spine's order of <paramref name="mine"/> once they have all shown up. The test database
    /// can hold other loadouts too, so everything else is left out.
    /// </summary>
    private async Task<LoadoutId[]> ItemsOf(SpineViewModel spine, params Loadout.ReadOnly[] mine)
    {
        var wanted = mine.Select(loadout => loadout.LoadoutId).ToHashSet();
        LoadoutId[] shown = [];
        for (var i = 0; i < 200 && shown.Length < wanted.Count; i++)
        {
            await Task.Delay(25);
            // The spine changes its collection on the UI thread; reading it anywhere else races the changes.
            shown = await OnUi(() => Task.FromResult(spine.LoadoutSpineItems
                .Select(item => item.LoadoutBadgeViewModel!.LoadoutValue.Value.LoadoutId)
                .Where(wanted.Contains)
                .ToArray()));
        }
        return shown;
    }

    private void SaveOrder(params LoadoutId[] order) =>
        Provider.GetRequiredService<ISettingsManager>().Update<LoadoutSpineOrderSettings>(_ =>
            new LoadoutSpineOrderSettings { LoadoutIds = order.Select(id => id.Value.Value).ToImmutableList() });

    private LoadoutSpineOrderSettings SavedOrder() => Provider.GetRequiredService<ISettingsManager>().Get<LoadoutSpineOrderSettings>();

    [Fact]
    public void Move_TakesItemFromOneIndexToAnother()
    {
        var order = new[] { Id(1), Id(2), Id(3), Id(4) };

        LoadoutSpineOrder.Move(order, 0, 2).Should().Equal(Id(2), Id(3), Id(1), Id(4));
        LoadoutSpineOrder.Move(order, 3, 1).Should().Equal(Id(1), Id(4), Id(2), Id(3));
        LoadoutSpineOrder.Move(order, 1, 1).Should().Equal(order);
    }

    [Fact]
    public void Move_RejectsIndicesOutsideTheList()
    {
        var order = new[] { Id(1), Id(2) };

        var act = () => LoadoutSpineOrder.Move(order, 0, 2);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task SavedOrderDecidesPositionAndNewLoadoutsComeLast()
    {
        var a = await SeedLoadoutRow(GameStore.Steam, "a");
        var b = await SeedLoadoutRow(GameStore.Steam, "b");
        var c = await SeedLoadoutRow(GameStore.Steam, "c");

        // c before a; b is not listed so it follows them
        SaveOrder(c.LoadoutId, a.LoadoutId);

        var (spine, activation) = await CreateSpine();
        using (activation)
        {
            (await ItemsOf(spine, a, b, c)).Should().Equal(c.LoadoutId, a.LoadoutId, b.LoadoutId);
        }
    }

    [Fact]
    public async Task MoveLoadoutSavesTheOrderAndSurvivesANewViewModel()
    {
        var a = await SeedLoadoutRow(GameStore.Steam, "a");
        var b = await SeedLoadoutRow(GameStore.Steam, "b");
        var c = await SeedLoadoutRow(GameStore.Steam, "c");
        SaveOrder(a.LoadoutId, b.LoadoutId, c.LoadoutId);
        var mine = new[] { a, b, c }.Select(l => l.LoadoutId.Value.Value).ToHashSet();

        var (spine, activation) = await CreateSpine();
        using (activation)
        {
            await ItemsOf(spine, a, b, c);
            await OnUi(() => spine.MoveLoadout(0, 2));
            (await ItemsOf(spine, a, b, c)).Should().Equal(b.LoadoutId, c.LoadoutId, a.LoadoutId);
        }

        SavedOrder().LoadoutIds.Where(id => mine.Contains(id)).Should().Equal(b.LoadoutId.Value.Value, c.LoadoutId.Value.Value, a.LoadoutId.Value.Value);

        var (rebuilt, rebuiltActivation) = await CreateSpine();
        using (rebuiltActivation)
        {
            (await ItemsOf(rebuilt, a, b, c)).Should().Equal(b.LoadoutId, c.LoadoutId, a.LoadoutId);
        }
    }

    [Fact]
    public async Task IdsOfDeletedLoadoutsAreIgnoredAndPrunedOnNextSave()
    {
        var a = await SeedLoadoutRow(GameStore.Steam, "a");
        var b = await SeedLoadoutRow(GameStore.Steam, "b");
        var gone = Id(0xDEAD);
        SaveOrder(b.LoadoutId, gone, a.LoadoutId);

        var (spine, activation) = await CreateSpine();
        using (activation)
        {
            (await ItemsOf(spine, b, a)).Should().Equal(b.LoadoutId, a.LoadoutId);

            await OnUi(() => spine.MoveLoadout(0, 1));
            var saved = SavedOrder().LoadoutIds;
            saved.Should().NotContain(gone.Value.Value);
            saved.Where(id => id == a.LoadoutId.Value.Value || id == b.LoadoutId.Value.Value)
                .Should().Equal(a.LoadoutId.Value.Value, b.LoadoutId.Value.Value);
        }
    }
}
