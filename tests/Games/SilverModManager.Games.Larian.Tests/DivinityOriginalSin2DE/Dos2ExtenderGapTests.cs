using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.DiagnosticSystem;
using SilverModManager.Games.Larian.Engine.Emitters;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Larian.Tests.DivinityOriginalSin2DE;

/// <summary>
/// OsiTools loads from a <c>DXGI.dll</c> beside the game's executable, which Wine needs an override for. The app
/// does not write that override yet, and says so rather than leaving the player to find out in the game.
/// </summary>
public class Dos2ExtenderGapTests(ITestOutputHelper helper) : Dos2Fixture(helper)
{
    private static readonly DiagnosticId ExtenderGapId = new("SilverModManager.Games.Larian.BaldursGate3", number: 18);

    private async Task<List<Diagnostic>> ExtenderGap(Loadout.ReadOnly loadout) =>
        (await Game.DiagnosticEmitters.OfType<LarianDependencyEmitter>().Single().Diagnose(Refresh(loadout), CancellationToken.None).ToListAsync())
        .Where(d => d.Id == ExtenderGapId).ToList();

    [Fact]
    public async Task ADxgiDllBesideTheGameBinaryIsReportedAsUnconfigured()
    {
        await WriteProfile("Alice", GameEntries);
        var loadout = await CreateLoadout();
        var dll = OnDisk(new GamePath(LocationId.Game, "DefEd/bin/DXGI.dll"));
        dll.Parent.CreateDirectory();
        await dll.WriteAllTextAsync("osi tools updater");

        var diagnostic = (await ExtenderGap(loadout)).Should().ContainSingle().Subject;

        diagnostic.Severity.Should().Be(DiagnosticSeverity.Suggestion);
        diagnostic.FormatSummary(ServiceProvider.GetRequiredService<IDiagnosticWriter>())
            .Should().Contain("Divinity: Original Sin 2").And.Contain("OsiTools").And.Contain("Wine override");
    }

    [Fact]
    public async Task NothingIsReportedWithoutIt()
    {
        await WriteProfile("Alice", GameEntries);
        var loadout = await CreateLoadout();

        (await ExtenderGap(loadout)).Should().BeEmpty();
    }
}
