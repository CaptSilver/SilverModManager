using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Backend;
using NexusMods.CrossPlatform;
using NexusMods.DataModel.GameInstalled;
using NexusMods.Games.Generic;
using NexusMods.Games.RedEngine;
using NexusMods.Games.RedEngine.Cyberpunk2077;
using NexusMods.Games.TestFramework;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.ElementComparers;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Sdk;
using NexusMods.Sdk.Loadouts;
using NexusMods.StandardGameLocators.TestHelpers;

namespace NexusMods.DataModel.Tests.GameInstalled;

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
