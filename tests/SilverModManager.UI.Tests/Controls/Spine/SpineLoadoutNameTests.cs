using System.Collections.ObjectModel;
using Avalonia.Threading;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using SilverModManager.App.UI.Controls.Spine;
using SilverModManager.App.UI.Controls.Spine.Buttons.Download;
using SilverModManager.App.UI.Controls.Spine.Buttons.Icon;
using SilverModManager.App.UI.WorkspaceAttachments;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.App.UI.Windows;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.UI.Tests.Controls.Spine;

public class SpineLoadoutNameTests(IServiceProvider provider) : AUiTest(provider)
{
    private async Task<(SpineViewModel Spine, IDisposable Activation)> CreateSpine()
    {
        var windows = Substitute.For<IWindowManager>();
        var controller = Substitute.For<IWorkspaceController>();
        controller.AllWorkspaces.Returns(new ReadOnlyObservableCollection<IWorkspaceViewModel>([]));
        windows.ActiveWorkspaceController.Returns(controller);

        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
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
        });
    }

    private static string GameName(Loadout.ReadOnly loadout) => loadout.InstallationInstance.Game.DisplayName;

    /// <summary>Waits until the spine's button for <paramref name="loadout"/> has the expected name.</summary>
    private async Task ButtonNameShouldBecome(SpineViewModel spine, Loadout.ReadOnly loadout, string expected)
    {
        string? shown = null;
        for (var i = 0; i < 200 && shown != expected; i++)
        {
            await Task.Delay(25);
            shown = await OnUi(() => Task.FromResult(spine.LoadoutSpineItems
                .FirstOrDefault(item => item.LoadoutBadgeViewModel!.LoadoutValue.Value.LoadoutId == loadout.LoadoutId)?.Name));
        }
        shown.Should().Be(expected);
    }

    [Fact]
    public async Task AGameWithOneLoadout_IsNamedAfterTheGameOnly()
    {
        var only = await SeedLoadoutRow(GameStore.Steam, "Only one");

        var (spine, activation) = await CreateSpine();
        using (activation)
        {
            await ButtonNameShouldBecome(spine, only, GameName(only));
        }
    }

    [Fact]
    public async Task AGameWithTwoLoadouts_NamesBothButtonsAfterTheGameAndTheLoadout()
    {
        var first = await SeedLoadoutRow(GameStore.Steam, "First");
        var second = await SeedSiblingLoadoutRow(first, "Second");

        var (spine, activation) = await CreateSpine();
        using (activation)
        {
            await ButtonNameShouldBecome(spine, first, $"{GameName(first)} - First");
            await ButtonNameShouldBecome(spine, second, $"{GameName(second)} - Second");
        }
    }

    [Fact]
    public async Task AddingASecondLoadout_RelabelsTheExistingButton()
    {
        var first = await SeedLoadoutRow(GameStore.Steam, "First");

        var (spine, activation) = await CreateSpine();
        using (activation)
        {
            await ButtonNameShouldBecome(spine, first, GameName(first));

            await SeedSiblingLoadoutRow(first, "Second");

            await ButtonNameShouldBecome(spine, first, $"{GameName(first)} - First");
        }
    }

    [Fact]
    public async Task DeletingBackToOneLoadout_DropsTheSuffix()
    {
        var first = await SeedLoadoutRow(GameStore.Steam, "First");
        var second = await SeedSiblingLoadoutRow(first, "Second");

        var (spine, activation) = await CreateSpine();
        using (activation)
        {
            await ButtonNameShouldBecome(spine, first, $"{GameName(first)} - First");

            using (var tx = Connection.BeginTransaction())
            {
                tx.Delete(second.LoadoutId, recursive: false);
                await tx.Commit();
            }

            await ButtonNameShouldBecome(spine, first, GameName(first));
        }
    }

    [Fact]
    public async Task LoadoutsOfDifferentInstallations_DoNotSuffixEachOther()
    {
        var one = await SeedLoadoutRow(GameStore.Steam, "One");
        var other = await SeedLoadoutRow(GameStore.Steam, "Other");

        var (spine, activation) = await CreateSpine();
        using (activation)
        {
            await ButtonNameShouldBecome(spine, one, GameName(one));
            await ButtonNameShouldBecome(spine, other, GameName(other));
        }
    }
}
