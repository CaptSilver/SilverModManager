using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Pages.Diagnostics;
using SilverModManager.App.UI.Windows;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// The Set aside button on a diagnostic that names a stray file: it asks, names the files and the group they go to,
/// and says so when the apply that follows the move did not finish.
/// </summary>
public class SetAsideActionHandlerTests(IServiceProvider provider) : AUiTest(provider)
{
    private static readonly GamePath Dll = new(LocationId.Game, "Data/SKSE/Plugins/old.dll");
    private static readonly GamePath DiskOnly = new(LocationId.Game, "Data/SKSE/Plugins/disk-only.dll");

    private sealed record Rig(
        IDiagnosticActionHandler Handler,
        ISetAsideFiles SetAside,
        IWindowManager Windows,
        IErrorPresenter Presenter,
        IWindowNotificationService Toasts,
        IDiagnosticManager Diagnostics);

    private Rig Build(ButtonDefinitionId answer, bool applied = true)
    {
        var presenter = Substitute.For<IErrorPresenter>();
        presenter.Report(Arg.Any<Exception>(), Arg.Any<string>()).Returns(Task.CompletedTask);
        var toasts = Substitute.For<IWindowNotificationService>();
        var windows = Substitute.For<IWindowManager>();
        windows.ShowDialog(Arg.Any<IDialog>(), Arg.Any<DialogWindowType>()).Returns(Task.FromResult(new StandardDialogResult { ButtonId = answer }));
        var diagnostics = Substitute.For<IDiagnosticManager>();
        var setAside = Substitute.For<ISetAsideFiles>();
        setAside.SetAside(Arg.Any<Loadout.ReadOnly>(), Arg.Any<IReadOnlyList<GamePath>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new SetAsideResult((IReadOnlyList<GamePath>)call[1], LoadoutItemGroupId.From(EntityId.From(0x42)), TxId.From(1), applied)));
        var services = new ServiceCollection()
            .AddSingleton(Connection)
            .AddSingleton(setAside)
            .AddSingleton(presenter)
            .AddSingleton(toasts)
            .BuildServiceProvider();
        return new Rig(new SetAsideActionHandler(services, windows, diagnostics), setAside, windows, presenter, toasts, diagnostics);
    }

    [Fact]
    public async Task AsksAndNamesTheFiles()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Set Aside Asks");
        var overrides = await SeedGroup(loadout.LoadoutId, "Overrides", files: [Dll]);
        var rig = Build(ButtonDefinitionId.Cancel);

        await rig.Handler.ExecuteAsync(loadout.LoadoutId, DiagnosticActions.SetAsideLoadoutFiles([Dll], overrides.Value).Arguments);

        await rig.Windows.Received(1).ShowDialog(Arg.Any<IDialog>(), DialogWindowType.Modal);
        var text = SetAsideText.For([Dll]);
        text.Should().Contain("old.dll").And.Contain(ISetAsideFiles.GroupName).And.Contain("turn the group on");
    }

    [Fact]
    public async Task CancelWritesNothing()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Set Aside Cancel");
        var overrides = await SeedGroup(loadout.LoadoutId, "Overrides", files: [Dll]);
        var rig = Build(ButtonDefinitionId.Cancel);

        await rig.Handler.ExecuteAsync(loadout.LoadoutId, DiagnosticActions.SetAsideLoadoutFiles([Dll], overrides.Value).Arguments);

        await rig.SetAside.DidNotReceiveWithAnyArgs().SetAside(default, default!, default);
        await rig.Diagnostics.DidNotReceiveWithAnyArgs().RefreshAsync(default);
    }

    [Fact]
    public async Task AcceptCallsSetAsideAndRefreshes()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Set Aside Accept");
        var overrides = await SeedGroup(loadout.LoadoutId, "Overrides", files: [Dll]);
        var rig = Build(ButtonDefinitionId.Accept);

        await rig.Handler.ExecuteAsync(loadout.LoadoutId, DiagnosticActions.SetAsideLoadoutFiles([Dll], overrides.Value).Arguments);

        await rig.SetAside.Received(1).SetAside(Arg.Any<Loadout.ReadOnly>(), Arg.Is<IReadOnlyList<GamePath>>(p => p.SequenceEqual(new[] { Dll })), Arg.Any<CancellationToken>());
        rig.Toasts.Received().ShowToast(Arg.Is<string>(t => t.Contains("1 file set aside")), Arg.Any<ToastNotificationVariant>(), Arg.Any<TimeSpan?>(), Arg.Any<DialogButtonDefinition[]?>(), Arg.Any<Action<ButtonDefinitionId>?>());
        await rig.Diagnostics.Received(1).RefreshAsync(loadout.LoadoutId);
    }

    [Fact]
    public async Task APathWithNoRowIsSkippedAndNamed()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Set Aside Disk Only");
        var overrides = await SeedGroup(loadout.LoadoutId, "Overrides", files: [Dll]);
        var rig = Build(ButtonDefinitionId.Accept);

        await rig.Handler.ExecuteAsync(loadout.LoadoutId, DiagnosticActions.SetAsideLoadoutFiles([Dll, DiskOnly], overrides.Value).Arguments);

        await rig.SetAside.Received(1).SetAside(Arg.Any<Loadout.ReadOnly>(), Arg.Is<IReadOnlyList<GamePath>>(p => p.SequenceEqual(new[] { Dll })), Arg.Any<CancellationToken>());
        rig.Toasts.Received().ShowToast(Arg.Is<string>(t => t.Contains("disk-only.dll") && t.Contains("offers Set aside")), Arg.Any<ToastNotificationVariant>(), Arg.Any<TimeSpan?>(), Arg.Any<DialogButtonDefinition[]?>(), Arg.Any<Action<ButtonDefinitionId>?>());
    }

    [Fact]
    public async Task ARefusalGoesToThePresenter()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Set Aside Refusal");
        var overrides = await SeedGroup(loadout.LoadoutId, "Overrides", files: [Dll]);
        var rig = Build(ButtonDefinitionId.Accept);
        rig.SetAside.SetAside(Arg.Any<Loadout.ReadOnly>(), Arg.Any<IReadOnlyList<GamePath>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<SetAsideResult>(new OverridesMoveRefusedException("not there")));

        await rig.Handler.ExecuteAsync(loadout.LoadoutId, DiagnosticActions.SetAsideLoadoutFiles([Dll], overrides.Value).Arguments);

        await rig.Presenter.Received(1).Report(Arg.Is<Exception>(e => e is OverridesMoveRefusedException), Arg.Any<string>());
    }

    [Fact]
    public async Task AFailedApplyAfterTheMoveIsReportedNotHidden()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Set Aside Apply Failed");
        var overrides = await SeedGroup(loadout.LoadoutId, "Overrides", files: [Dll]);
        var rig = Build(ButtonDefinitionId.Accept, applied: false);

        await rig.Handler.ExecuteAsync(loadout.LoadoutId, DiagnosticActions.SetAsideLoadoutFiles([Dll], overrides.Value).Arguments);

        rig.Toasts.Received().ShowToast(Arg.Is<string>(t => t.Contains("the apply did not finish") && t.Contains("run Apply")), ToastNotificationVariant.Failure, Arg.Any<TimeSpan?>(), Arg.Any<DialogButtonDefinition[]?>(), Arg.Any<Action<ButtonDefinitionId>?>());
        await rig.Diagnostics.Received(1).RefreshAsync(loadout.LoadoutId);
    }

    [Fact]
    public void ItAsksSoTheCommandLineRefusesIt()
    {
        Build(ButtonDefinitionId.Accept).Handler.RunsUnattended.Should().BeFalse();
    }
}
