using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.SingleProcess;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// <c>silvermod loadouts list</c> against a loadout that has items.
/// </summary>
public class LoadoutsListVerbTests(IServiceProvider provider)
{
    private async Task<string> SeedLoadoutWithItems(int itemCount)
    {
        var connection = provider.GetRequiredService<IConnection>();
        var name = "Items Count Loadout " + Guid.NewGuid();

        using var tx = connection.BeginTransaction();
        var metadata = new GameInstallMetadata.New(tx)
        {
            Path = "/tmp/loadouts-list-tests-" + Guid.NewGuid(),
            Name = "Items Count Game",
            Store = GameStore.Unknown,
            Game = GameId.From("ItemsCountGame-" + Guid.NewGuid()),
        };
        var loadout = new Loadout.New(tx)
        {
            Name = name,
            ShortName = "IC" + Guid.NewGuid().ToString("N")[..6],
            InstallationId = metadata,
            LoadoutKind = LoadoutKind.Default,
            Revision = 0,
            GameVersion = VanityVersion.From("Unknown"),
        };
        for (var i = 0; i < itemCount; i++)
        {
            _ = new LoadoutItemGroup.New(tx, out var groupId)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, groupId)
                {
                    Name = $"Item {i}",
                    LoadoutId = loadout,
                },
            };
        }
        await tx.Commit();
        return name;
    }

    [Fact]
    public async Task ItemsColumnHoldsTheCountNotTheCollectionTypeName()
    {
        var name = await SeedLoadoutWithItems(3);

        var renderer = new LoggingRenderer();
        var exitCode = await provider.GetRequiredService<CommandLineConfigurator>()
            .RunAsync(["loadouts", "list"], renderer, CancellationToken.None);

        exitCode.Should().Be(0);
        renderer.LastTableColumns.Should().Equal("Id", "Name", "Game", "Version", "Store", "Items");
        var row = renderer.LastCellsAsStrings().Select(r => r.ToArray()).Single(cells => cells[1] == name);
        row[5].Should().Be("3");
        row[5].Should().NotContain("Entities<");
    }
}
