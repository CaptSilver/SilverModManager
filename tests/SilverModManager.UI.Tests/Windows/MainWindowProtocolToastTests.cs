using Avalonia.Threading;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.App.UI.Windows;
using SilverModManager.CLI;
using SilverModManager.Sdk.EventBus;
using SilverModManager.Sdk.Library;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Tests.Framework;

namespace SilverModManager.UI.Tests.Windows;

/// <summary>
/// What the main window tells the user about link handling and link installs: a scheme taken from or left with
/// another app, a package installed from a link, and the two refusals a link can end in. The window shows
/// its toasts on a substituted notification service.
/// </summary>
[Collection(HeadlessUi.GlassSettingsCollection)]
public class MainWindowProtocolToastTests(IServiceProvider provider) : AUiTest(provider)
{
    private readonly IWindowNotificationService _toasts = Substitute.For<IWindowNotificationService>();

    private IEventBus Bus => Provider.GetRequiredService<IEventBus>();

    private async Task OpenWindow()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var viewModel = ActivatorUtilities.CreateInstance<MainWindowViewModel>(Provider, _toasts);
            var window = Track(new MainWindow());
            ShowRendered(window);
            window.ViewModel = viewModel;
            Render(window);
        });
    }

    private void ToastShown(string text, ToastNotificationVariant variant) =>
        _toasts.Received(1).ShowToast(text, variant, Arg.Any<TimeSpan?>(), Arg.Any<SilverModManager.UI.Sdk.Dialog.DialogButtonDefinition[]?>(), Arg.Any<Action<SilverModManager.UI.Sdk.Dialog.ButtonDefinitionId>?>());

    private static string Holder() => $"holder-{Guid.NewGuid():N}.desktop";

    [Fact]
    public async Task ASchemeTakenFromAnotherApp_SaysWhichAppHadIt()
    {
        await OpenWindow();

        Bus.Send(new CliMessages.ProtocolHandlerTaken("ror2mm", "r2modman.desktop"));

        await EventuallyOnUi(() => ToastShown("ror2mm:// links were opened by r2modman.desktop; this app opens them now.", ToastNotificationVariant.Neutral));
    }

    [Fact]
    public async Task ASchemeLeftWithAnotherApp_SaysHowToTakeItAndRemembersTheUserWasTold()
    {
        var holder = Holder();
        var notices = Provider.GetRequiredService<ProtocolNotices>();
        await OpenWindow();

        notices.ReportHeldElsewhere("ror2mm", holder);

        await EventuallyOnUi(() => ToastShown($"ror2mm:// links open {holder}. Turn the ror2mm:// link setting off and on, or run associate-ror2mm, to open them here.", ToastNotificationVariant.Neutral));
        await EventuallyOnUi(() => notices.Pending.Should().NotContain(n => n.Holder == holder));
    }

    [Fact]
    public async Task ANoticeReportedBeforeTheWindowOpened_IsShownOnceWhenItOpens()
    {
        var holder = Holder();
        var notices = Provider.GetRequiredService<ProtocolNotices>();
        notices.ReportHeldElsewhere("nxm", holder);

        await OpenWindow();

        await EventuallyOnUi(() => ToastShown($"nxm:// links open {holder}. Turn the nxm:// link setting off and on, or run associate-nxm, to open them here.", ToastNotificationVariant.Neutral));
        notices.Pending.Should().NotContain(n => n.Holder == holder);
    }

    [Fact]
    public async Task APackageInstalledFromALink_SaysWhatWentWhere()
    {
        var item = await AddLibraryItem("REPOLib");
        await OpenWindow();

        Bus.Send(new CliMessages.ModInstalled(item, "My loadout"));

        await EventuallyOnUi(() => ToastShown("Installed REPOLib into My loadout", ToastNotificationVariant.Success));
    }

    [Fact]
    public async Task ARefusedLink_ShowsTheRefusalAsAFailure()
    {
        await OpenWindow();

        Bus.Send(new CliMessages.ModDownloadFailed(new FailureReason.Refused("Thunderstore has no package Zehs-Nothing")));

        await EventuallyOnUi(() => ToastShown("Thunderstore has no package Zehs-Nothing", ToastNotificationVariant.Failure));
    }

    [Theory]
    [InlineData(new[] { "REPO", "Valheim" }, "REPO and Valheim")]
    [InlineData(new[] { "REPO", "Valheim", "Lethal Company" }, "REPO, Valheim and Lethal Company")]
    public async Task AnAmbiguousLink_NamesTheGamesAndSendsTheUserToThePageOfTheOneTheyMean(string[] games, string listed)
    {
        await OpenWindow();

        Bus.Send(new CliMessages.ModDownloadFailed(new FailureReason.Ambiguous("Zehs-REPOLib", games)));

        await EventuallyOnUi(() => ToastShown($"Zehs-REPOLib is listed for {listed}; install it from the Thunderstore page of the game you mean.", ToastNotificationVariant.Failure));
    }

    private async Task<LibraryItem.ReadOnly> AddLibraryItem(string name)
    {
        using var tx = Connection.BeginTransaction();
        var id = tx.TempId();
        _ = new LibraryItem.New(tx, id) { Name = name };
        var result = await tx.Commit();
        return LibraryItem.Load(result.Db, result[id]);
    }
}
