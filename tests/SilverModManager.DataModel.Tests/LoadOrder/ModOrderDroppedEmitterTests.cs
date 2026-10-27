using System.Collections.Frozen;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.Tests.LoadOrder;

public class ModOrderDroppedEmitterTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<ModOrderDroppedEmitterTests>(helper)
{
    private async Task<LoadoutItemGroupId> AddContainer(LoadoutId loadoutId, string name)
    {
        using var tx = Connection.BeginTransaction();
        var temp = AddEmptyGroup(tx, loadoutId, name);
        var result = await tx.Commit();
        return LoadoutItemGroupId.From(result[temp].Value);
    }

    private async Task AddRow(LoadoutId loadoutId, LoadoutItemGroupId container, string source, string item, string reason = "no installed mod matches it")
    {
        using var tx = Connection.BeginTransaction();
        _ = new ModOrderDroppedEntry.New(tx)
        {
            LoadoutId = loadoutId,
            ContainerId = container,
            Source = source,
            Item = item,
            Reason = reason,
            Page = "Load Order",
        };
        await tx.Commit();
    }

    private async Task<Diagnostic[]> Diagnose(Loadout.ReadOnly loadout)
    {
        var emitter = new ModOrderDroppedEmitter();
        var result = new List<Diagnostic>();
        await foreach (var diagnostic in emitter.Diagnose(Loadout.Load(Connection.Db, loadout.LoadoutId), FrozenDictionary<GamePath, SyncNode>.Empty, CancellationToken.None)) result.Add(diagnostic);
        return result.ToArray();
    }

    [Fact]
    public async Task OneWarningPerDroppedRow()
    {
        var loadout = await CreateLoadout();
        var container = await AddContainer(loadout.LoadoutId, "Collection X");
        await AddRow(loadout.LoadoutId, container, "collection X", "Ghost");
        await AddRow(loadout.LoadoutId, container, "collection X", "Phantom");

        var diagnostics = await Diagnose(loadout);

        diagnostics.Should().HaveCount(2).And.OnlyContain(d => d.Severity == DiagnosticSeverity.Warning);
        var summaries = diagnostics.Select(d => d.FormatSummary(new PlainDiagnosticWriter())).ToArray();
        summaries.Should().Contain(s => s.Contains("Ghost") && s.Contains("collection X"));
        summaries.Should().Contain(s => s.Contains("Phantom"));
    }

    [Fact]
    public async Task NoRowsNoCards()
    {
        var loadout = await CreateLoadout();
        (await Diagnose(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task RemovingTheCollectionRemovesItsCards()
    {
        var loadout = await CreateLoadout();
        var gone = await AddContainer(loadout.LoadoutId, "Gone");
        var kept = await AddContainer(loadout.LoadoutId, "Kept");
        await AddRow(loadout.LoadoutId, gone, "collection Gone", "GoneItem");
        await AddRow(loadout.LoadoutId, kept, "collection Kept", "KeptItem");

        await LoadoutManager.RemoveItems([gone]);

        var diagnostics = await Diagnose(loadout);
        diagnostics.Should().ContainSingle().Which.FormatSummary(new PlainDiagnosticWriter()).Should().Contain("KeptItem");
    }

    [Fact]
    public void TheCardNamesThePage()
    {
        var card = ADroppedItemEmitter.Card(new("Ghost", "collection X", "no installed mod matches it", "Load Order"));
        card.FormatDetails(new PlainDiagnosticWriter()).Should().Contain("Load Order page");
    }

    [Fact]
    public void TheEmitterIsRegisteredForEveryGame()
    {
        ServiceProvider.GetServices<IDiagnosticEmitter>().OfType<ModOrderDroppedEmitter>().Should().ContainSingle();
    }
}
