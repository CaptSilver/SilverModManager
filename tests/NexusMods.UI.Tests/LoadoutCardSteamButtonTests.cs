using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.App.UI.Controls.LoadoutCard;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using ReactiveUI;
using Loadout = NexusMods.Sdk.Loadouts.Loadout;

namespace NexusMods.UI.Tests;

public class LoadoutCardSteamButtonTests(IServiceProvider provider) : AUiTest(provider)
{
    private async Task<Loadout.ReadOnly> SeedLoadout(GameStore store)
    {
        var installation = Provider.GetRequiredService<IGameRegistry>().LocateGameInstallations().First();
        using var tx = Connection.BeginTransaction();
        var metadata = new GameInstallMetadata.New(tx)
        {
            Path = installation.LocatorResult.Path.ToString(),
            Name = installation.Game.DisplayName,
            Store = store,
            GameId = installation.Game.NexusModsGameId.Value,
        };
        var loadout = new Loadout.New(tx)
        {
            Name = $"Steam Button {store.Value}",
            ShortName = "ZZZ",
            InstallationId = metadata,
            LoadoutKind = LoadoutKind.Default,
            Revision = 0,
            GameVersion = VanityVersion.From("Unknown"),
        };
        var result = await tx.Commit();
        return result.Remap(loadout);
    }

    private LoadoutCardViewModel CardFor(Loadout.ReadOnly loadout) => new(loadout, Connection, Provider)
    {
        VisitLoadoutCommand = ReactiveCommand.Create(() => { }),
    };

    [Fact]
    public async Task Test_SteamInstallation_CanUseFromSteam()
    {
        var card = CardFor(await SeedLoadout(GameStore.Steam));

        card.CanUseFromSteam.Should().BeTrue();
    }

    [Fact]
    public async Task Test_GogInstallation_CannotUseFromSteam()
    {
        var card = CardFor(await SeedLoadout(GameStore.GOG));

        card.CanUseFromSteam.Should().BeFalse();
    }
}
