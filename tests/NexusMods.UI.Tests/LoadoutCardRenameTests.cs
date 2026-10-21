using System.Reactive.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.App.UI.Controls.LoadoutCard;
using NexusMods.App.UI.Dialog;
using NexusMods.App.UI.Dialog.Enums;
using NexusMods.Sdk;
using NexusMods.App.UI.Windows;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.UI.Sdk.Dialog;
using NexusMods.UI.Sdk.Dialog.Enums;
using NSubstitute;
using ReactiveUI;
using Loadout = NexusMods.Sdk.Loadouts.Loadout;

namespace NexusMods.UI.Tests;

public class LoadoutCardRenameTests(IServiceProvider provider) : AUiTest(provider)
{
    private sealed class WindowManagerProvider(IServiceProvider inner, IWindowManager windows) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IWindowManager) ? windows : inner.GetService(serviceType);
    }

    private async Task<Loadout.ReadOnly[]> SeedLoadouts(GameInstallation installation, params string[] names)
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
        var loadouts = names.Select(name => new Loadout.New(tx)
        {
            Name = name,
            ShortName = "ZZZ",
            InstallationId = metadata,
            LoadoutKind = LoadoutKind.Default,
            Revision = 0,
            GameVersion = VanityVersion.From("Unknown"),
        }).ToArray();
        var result = await tx.Commit();
        return loadouts.Select(l => result.Remap(l)).ToArray();
    }

    private (LoadoutCardViewModel Card, IWindowManager Windows) CardFor(Loadout.ReadOnly loadout, ButtonDefinitionId button, string input)
    {
        var windows = Substitute.For<IWindowManager>();
        windows.ShowDialog(Arg.Any<IDialog>(), Arg.Any<DialogWindowType>())
            .Returns(new StandardDialogResult { ButtonId = button, InputText = input });
        var card = new LoadoutCardViewModel(loadout, Connection, new WindowManagerProvider(Provider, windows))
        {
            VisitLoadoutCommand = ReactiveCommand.Create(() => { }),
        };
        return (card, windows);
    }

    private string NameOf(Loadout.ReadOnly loadout) => Loadout.Load(Connection.Db, loadout.LoadoutId).Name;

    private GameInstallation Installation => Provider.GetRequiredService<IGameRegistry>().LocateGameInstallations().First();

    [Fact]
    public async Task Test_Rename_StoresTheNameTheDialogReturned()
    {
        var loadouts = await SeedLoadouts(Installation, "Rename Plain");
        var (card, windows) = CardFor(loadouts[0], ButtonDefinitionId.Accept, "  Renamed Plain  ");

        await card.RenameLoadoutCommand.Execute();

        await windows.Received(1).ShowDialog(Arg.Any<IDialog>(), DialogWindowType.Modal);
        NameOf(loadouts[0]).Should().Be("Renamed Plain");
    }

    [Fact]
    public async Task Test_Rename_GoesThroughUniqueNaming()
    {
        var loadouts = await SeedLoadouts(Installation, "Rename Taken", "Rename Other");
        var (card, _) = CardFor(loadouts[1], ButtonDefinitionId.Accept, "rename taken");

        await card.RenameLoadoutCommand.Execute();

        NameOf(loadouts[1]).Should().Be("rename taken (2)");
        NameOf(loadouts[0]).Should().Be("Rename Taken");
    }

    [Fact]
    public async Task Test_Rename_CancelledDialogChangesNothing()
    {
        var loadouts = await SeedLoadouts(Installation, "Rename Cancelled");
        var (card, _) = CardFor(loadouts[0], ButtonDefinitionId.Cancel, "Something Else");

        await card.RenameLoadoutCommand.Execute();

        NameOf(loadouts[0]).Should().Be("Rename Cancelled");
    }

    [Fact]
    public async Task Test_Rename_BlankNameChangesNothing()
    {
        var loadouts = await SeedLoadouts(Installation, "Rename Blank");
        var (card, _) = CardFor(loadouts[0], ButtonDefinitionId.Accept, "   ");

        await card.RenameLoadoutCommand.Execute();

        NameOf(loadouts[0]).Should().Be("Rename Blank");
    }
}
