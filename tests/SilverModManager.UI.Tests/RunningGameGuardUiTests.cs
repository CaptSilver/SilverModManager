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
using SilverModManager.UI.Tests.Helpers;
using ReactiveUI;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests;

public class RunningGameGuardUiTests(IServiceProvider provider) : AUiTest(provider)
{
    private static string[] ShownTitles(PresenterRig rig) => rig.Dialogs.Select(d => d.WindowTitle).ToArray();

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
    public async Task RunOrExplain_ShowsTheDialog_InsteadOfLettingTheExceptionEscape()
    {
        var rig = new PresenterRig(Provider);

        var ran = await MessageBoxOkViewModel.RunOrExplain(
            rig.Services,
            () => throw new ExecutableInUseException("running")
        );

        ran.Should().BeFalse();
        ShownTitles(rig).Should().Equal("The game is still running");
    }

    [Fact]
    public async Task RunOrExplain_ShowsNothing_WhenTheActionSucceeds()
    {
        var rig = new PresenterRig(Provider);

        var ran = await MessageBoxOkViewModel.RunOrExplain(rig.Services, () => Task.CompletedTask);

        ran.Should().BeTrue();
        ShownTitles(rig).Should().BeEmpty();
    }

    [Fact]
    public async Task RunOrExplain_DoesNotSwallowOtherExceptions()
    {
        var act = () => MessageBoxOkViewModel.RunOrExplain(
            new PresenterRig(Provider).Services,
            () => throw new InvalidOperationException("boom")
        );

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task RunOrExplain_LeavesAPreflightRefusalToTheCaller()
    {
        // The remove-game flow answers this one with its own choice, so it must still reach that catch.
        var rig = new PresenterRig(Provider);

        var act = () => MessageBoxOkViewModel.RunOrExplain(rig.Services, () => throw new SynchronizerPreflightException("read-only"));

        await act.Should().ThrowAsync<SynchronizerPreflightException>();
        rig.Dialogs.Should().BeEmpty();
    }

    [Fact]
    public async Task DeletingALoadoutCard_WhileTheGameRuns_ShowsTheDialogAndKeepsTheLoadout()
    {
        var installation = Provider.GetRequiredService<IGameRegistry>().LocateGameInstallations().First();
        var loadout = await SeedLoadout(installation, "Running Delete");
        var rig = new PresenterRig(Provider);
        var card = new LoadoutCardViewModel(loadout, Connection, rig.Services)
        {
            VisitLoadoutCommand = ReactiveCommand.Create(() => { }),
        };

        using var running = Provider.GetRequiredService<ActiveLaunchSessions>().Register(installation);
        await card.DeleteLoadoutCommand.Execute();

        ShownTitles(rig).Should().Equal("The game is still running");
        card.IsDeleting.Should().BeFalse();
        var reloaded = Loadout.Load(Connection.Db, loadout.LoadoutId);
        reloaded.IsValid().Should().BeTrue();
        reloaded.IsVisible().Should().BeTrue();
    }
}
