using System.Reactive.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Exceptions;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.App.UI.Controls.LoadoutCard;
using SilverModManager.App.UI.Overlays;
using SilverModManager.App.UI.Overlays.Generic.MessageBox.Ok;
using SilverModManager.App.UI.Resources;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk.Games;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Loadouts;
using NSubstitute;
using ReactiveUI;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests;

public class RunningGameGuardUiTests(IServiceProvider provider) : AUiTest(provider)
{
    /// <summary>
    /// The real services, except that overlays go to a recorder instead of the (absent) window.
    /// </summary>
    private sealed class RecordingProvider(IServiceProvider inner, IOverlayController overlays) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IOverlayController) ? overlays : inner.GetService(serviceType);
    }

    private static IOverlayController RecordingOverlays()
    {
        var overlays = Substitute.For<IOverlayController>();
        return overlays;
    }

    private static string[] ShownTitles(IOverlayController overlays) => overlays.ReceivedCalls()
        .SelectMany(call => call.GetArguments())
        .OfType<MessageBoxOkViewModel>()
        .Select(vm => vm.Title)
        .ToArray();

    /// <summary>
    /// A loadout row on the installation without applying anything, since the test game has no files to apply.
    /// </summary>
    private async Task<Loadout.ReadOnly> SeedLoadout(GameInstallation installation, string name)
    {
        using var tx = Connection.BeginTransaction();
        var metadata = new GameInstallMetadata.New(tx)
        {
            Path = installation.LocatorResult.Path.ToString(),
            Name = installation.Game.DisplayName,
            Store = installation.LocatorResult.Store,
            Game = installation.Game.GameId,
            GameId = installation.Game.NexusModsGameId.Value,
        };
        var loadout = new Loadout.New(tx)
        {
            Name = name,
            ShortName = "ZZZ",
            InstallationId = metadata,
            LoadoutKind = LoadoutKind.Default,
            Revision = 0,
            GameVersion = VanityVersion.From("Unknown"),
        };
        var result = await tx.Commit();
        return result.Remap(loadout);
    }

    [Fact]
    public async Task RunOrExplainRunningGame_ShowsTheDialog_InsteadOfLettingTheExceptionEscape()
    {
        var overlays = RecordingOverlays();

        var ran = await MessageBoxOkViewModel.RunOrExplainRunningGame(
            new RecordingProvider(Provider, overlays),
            "Cyberpunk 2077",
            () => throw new ExecutableInUseException("running")
        );

        ran.Should().BeFalse();
        ShownTitles(overlays).Should().Equal(Language.ErrorGameAlreadyRunning_Title);
    }

    [Fact]
    public async Task RunOrExplainRunningGame_ShowsNothing_WhenTheActionSucceeds()
    {
        var overlays = RecordingOverlays();

        var ran = await MessageBoxOkViewModel.RunOrExplainRunningGame(new RecordingProvider(Provider, overlays), "Cyberpunk 2077", () => Task.CompletedTask);

        ran.Should().BeTrue();
        ShownTitles(overlays).Should().BeEmpty();
    }

    [Fact]
    public async Task RunOrExplainRunningGame_DoesNotSwallowOtherExceptions()
    {
        var act = () => MessageBoxOkViewModel.RunOrExplainRunningGame(
            new RecordingProvider(Provider, RecordingOverlays()),
            "Cyberpunk 2077",
            () => throw new InvalidOperationException("boom")
        );

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task DeletingALoadoutCard_WhileTheGameRuns_ShowsTheDialogAndKeepsTheLoadout()
    {
        var installation = Provider.GetRequiredService<IGameRegistry>().LocateGameInstallations().First();
        var loadout = await SeedLoadout(installation, "Running Delete");
        var overlays = RecordingOverlays();
        var card = new LoadoutCardViewModel(loadout, Connection, new RecordingProvider(Provider, overlays))
        {
            VisitLoadoutCommand = ReactiveCommand.Create(() => { }),
        };

        using var running = Provider.GetRequiredService<ActiveLaunchSessions>().Register(installation);
        await card.DeleteLoadoutCommand.Execute();

        ShownTitles(overlays).Should().Equal(Language.ErrorGameAlreadyRunning_Title);
        card.IsDeleting.Should().BeFalse();
        var reloaded = Loadout.Load(Connection.Db, loadout.LoadoutId);
        reloaded.IsValid().Should().BeTrue();
        reloaded.IsVisible().Should().BeTrue();
    }
}
