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
    public async Task OneCardForASourceThatDroppedSeveralEntries()
    {
        var loadout = await CreateLoadout();
        var container = await AddContainer(loadout.LoadoutId, "Collection X");
        await AddRow(loadout.LoadoutId, container, "collection X", "Ghost", "no mod matches 'Ghost'");
        await AddRow(loadout.LoadoutId, container, "collection X", "Phantom", "no mod matches 'Phantom'");

        var diagnostics = await Diagnose(loadout);

        var card = diagnostics.Should().ContainSingle().Which;
        card.Severity.Should().Be(DiagnosticSeverity.Warning);
        card.Title.Should().Be("Something was left out");
        card.FormatSummary(new PlainDiagnosticWriter()).Should().Be("2 entries of collection X's load order are not used");
        var details = card.FormatDetails(new PlainDiagnosticWriter());
        details.Should().Contain("Ghost").And.Contain("no mod matches 'Ghost'").And.Contain("Phantom").And.Contain("no mod matches 'Phantom'");
        details.Should().Contain("Load Order page");
    }

    [Fact]
    public async Task ASingleDroppedEntryKeepsItsOwnCard()
    {
        var loadout = await CreateLoadout();
        var container = await AddContainer(loadout.LoadoutId, "Collection X");
        await AddRow(loadout.LoadoutId, container, "collection X", "Ghost");

        var diagnostics = await Diagnose(loadout);

        diagnostics.Should().ContainSingle().Which.FormatSummary(new PlainDiagnosticWriter()).Should().Contain("Ghost").And.Contain("collection X");
    }

    [Fact]
    public async Task EachSourceGetsItsOwnCard()
    {
        var loadout = await CreateLoadout();
        var first = await AddContainer(loadout.LoadoutId, "Collection X");
        var second = await AddContainer(loadout.LoadoutId, "Collection Y");
        await AddRow(loadout.LoadoutId, first, "collection X", "Ghost");
        await AddRow(loadout.LoadoutId, first, "collection X", "Phantom");
        await AddRow(loadout.LoadoutId, second, "collection Y", "Wraith");
        await AddRow(loadout.LoadoutId, second, "collection Y", "Shade");

        var summaries = (await Diagnose(loadout)).Select(d => d.FormatSummary(new PlainDiagnosticWriter())).ToArray();

        summaries.Should().HaveCount(2);
        summaries.Should().Contain(s => s.Contains("collection X")).And.Contain(s => s.Contains("collection Y"));
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
