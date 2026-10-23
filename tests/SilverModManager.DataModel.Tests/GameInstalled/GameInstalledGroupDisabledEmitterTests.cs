using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Backend;
using SilverModManager.CrossPlatform;
using SilverModManager.DataModel.GameInstalled;
using SilverModManager.Games.Generic;
using SilverModManager.Games.RedEngine;
using SilverModManager.Games.RedEngine.Cyberpunk2077;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.DataModel.Tests.GameInstalled;

public class GameInstalledGroupDisabledEmitterTests(ITestOutputHelper helper)
    : ALoadoutDiagnosticEmitterTest<GameInstalledGroupDisabledEmitterTests, Cyberpunk2077Game, GameInstalledGroupDisabledEmitter>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        // The base class looks the emitter up by its own type; the app registers it only as an IDiagnosticEmitter.
        return base.AddServices(services)
            .AddSingleton<GameInstalledGroupDisabledEmitter>()
            .AddOSInterop()
            .AddRuntimeDependencies()
            .RemoveRuntimeDependencyChecker()
            .AddGenericGameSupport()
            .AddFakeToolRunner()
            .AddUniversalGameLocator<Cyberpunk2077Game>(new Version("1.61"))
            .AddRedEngineGames();
    }

    private async Task AddGroup(Loadout.ReadOnly loadout, bool disabled, params string[] itemNames)
    {
        using var tx = Connection.BeginTransaction();
        _ = new GameInstalledGroup.New(tx, out var groupId)
        {
            InstalledForId = loadout,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, groupId)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, groupId)
                {
                    Name = "Installed by the game",
                    LoadoutId = loadout.Id,
                },
            },
        };
        if (disabled) tx.Add(groupId, LoadoutItem.Disabled, Null.Instance);

        foreach (var name in itemNames)
        {
            _ = new GameInstalledItem.New(tx, out var itemId)
            {
                Source = "SkyrimCreation",
                SourceId = name,
                Confidence = "GameRecord",
                CanBeClaimed = false,
                DepotShipped = true,
                LoadoutItemGroup = new LoadoutItemGroup.New(tx, itemId)
                {
                    IsGroup = true,
                    LoadoutItem = new LoadoutItem.New(tx, itemId)
                    {
                        Name = name,
                        LoadoutId = loadout.Id,
                        ParentId = groupId,
                    },
                },
            };
        }
        await tx.Commit();
    }

    [Fact]
    public async Task NoGroup_NoDiagnostic()
    {
        var loadout = await CreateLoadout();
        await ShouldHaveNoDiagnostics(loadout);
    }

    [Fact]
    public async Task EnabledGroup_NoDiagnostic()
    {
        var loadout = await CreateLoadout();
        await AddGroup(Loadout.Load(Connection.Db, loadout), disabled: false, "ccOne", "ccTwo");
        await ShouldHaveNoDiagnostics(loadout);
    }

    [Fact]
    public async Task DisabledGroupWithoutItems_NoDiagnostic()
    {
        var loadout = await CreateLoadout();
        await AddGroup(Loadout.Load(Connection.Db, loadout), disabled: true);
        await ShouldHaveNoDiagnostics(loadout);
    }

    [Fact]
    public async Task DisabledGroupWithItems_WarnsAndNamesBothItems()
    {
        var loadout = await CreateLoadout();
        await AddGroup(Loadout.Load(Connection.Db, loadout), disabled: true, "ccOne", "ccTwo");

        var diagnostic = await GetSingleDiagnostic(loadout);

        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        var writer = ServiceProvider.GetRequiredService<IDiagnosticWriter>();
        var details = diagnostic.FormatDetails(writer);
        details.Should().Contain("ccOne").And.Contain("ccTwo");
        diagnostic.FormatSummary(writer).Should().Be("Content installed by the game is switched off");
    }
}
