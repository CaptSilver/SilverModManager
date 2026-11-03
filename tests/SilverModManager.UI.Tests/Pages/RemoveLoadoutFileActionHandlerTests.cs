using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Pages.Diagnostics;
using SilverModManager.App.UI.Windows;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// The delete button on a diagnostic that names a stray file: it removes the row of the named group and nothing else.
/// </summary>
public class RemoveLoadoutFileActionHandlerTests(IServiceProvider provider) : AUiTest(provider)
{
    private static readonly GamePath Dll = new(LocationId.Game, "Data/SKSE/Plugins/old.dll");

    private sealed record Rig(
        RemoveLoadoutFileActionHandler Handler,
        IErrorPresenter Presenter,
        IWindowNotificationService Toasts,
        IDiagnosticManager Diagnostics);

    private Rig Build(ButtonDefinitionId answer)
    {
        var presenter = Substitute.For<IErrorPresenter>();
        presenter.Report(Arg.Any<Exception>(), Arg.Any<string>()).Returns(Task.CompletedTask);
        var toasts = Substitute.For<IWindowNotificationService>();
        var windows = Substitute.For<IWindowManager>();
        windows.ShowDialog(Arg.Any<IDialog>(), Arg.Any<DialogWindowType>()).Returns(Task.FromResult(new StandardDialogResult { ButtonId = answer }));
        var diagnostics = Substitute.For<IDiagnosticManager>();
        var services = new ServiceCollection()
            .AddSingleton(Connection)
            .AddSingleton(Provider.GetRequiredService<ILoadoutFileRemover>())
            .AddSingleton(presenter)
            .AddSingleton(toasts)
            .BuildServiceProvider();
        return new Rig(new RemoveLoadoutFileActionHandler(services, windows, diagnostics), presenter, toasts, diagnostics);
    }

    private int RowsAt(Loadout.ReadOnly loadout) =>
        LoadoutItemWithTargetPath.FindByTargetPath(Connection.Db, Dll.ToGamePathParentTuple(loadout.Id)).Count;

    [Fact]
    public async Task AcceptingRemovesOnlyTheNamedGroupsRow()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Remove Stray Scoped");
        var strays = await SeedGroup(loadout.LoadoutId, "Overrides", files: [Dll]);
        await SeedGroup(loadout.LoadoutId, "Plugin mod", files: [Dll]);
        var rig = Build(ButtonDefinitionId.Accept);
        RowsAt(loadout).Should().Be(2);

        await rig.Handler.ExecuteAsync(loadout.LoadoutId, DiagnosticActions.RemoveLoadoutFile(Dll, strays.Value).Arguments);

        RowsAt(loadout).Should().Be(1, "the mod's row stays so its copy is what deploys");
        LoadoutItemWithTargetPath.FindByTargetPath(Connection.Db, Dll.ToGamePathParentTuple(loadout.Id)).Single().AsLoadoutItem().Parent.AsLoadoutItem().Name.Should().Be("Plugin mod");
        rig.Toasts.ReceivedWithAnyArgs(1).ShowToast(default!);
        await rig.Diagnostics.Received(1).RefreshAsync(loadout.LoadoutId);
    }

    [Fact]
    public async Task CancellingRemovesNothing()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Remove Stray Cancel");
        var strays = await SeedGroup(loadout.LoadoutId, "Overrides", files: [Dll]);
        var rig = Build(ButtonDefinitionId.Cancel);

        await rig.Handler.ExecuteAsync(loadout.LoadoutId, DiagnosticActions.RemoveLoadoutFile(Dll, strays.Value).Arguments);

        RowsAt(loadout).Should().Be(1);
        await rig.Diagnostics.DidNotReceiveWithAnyArgs().RefreshAsync(default);
    }

    [Fact]
    public async Task ARefusalGoesToThePresenter()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Remove Stray Refusal");
        var other = await SeedGroup(loadout.LoadoutId, "Elsewhere", files: [new GamePath(LocationId.Game, "other.dll")]);
        var rig = Build(ButtonDefinitionId.Accept);

        await rig.Handler.ExecuteAsync(loadout.LoadoutId, DiagnosticActions.RemoveLoadoutFile(Dll, other.Value).Arguments);

        await rig.Presenter.Received(1).Report(Arg.Is<Exception>(e => e is FileRemovalRefusedException), Arg.Any<string>());
        await rig.Diagnostics.DidNotReceiveWithAnyArgs().RefreshAsync(default);
    }
}
