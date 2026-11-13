using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.DiagnosticSystem;
using SilverModManager.Games.Larian.Engine.Emitters;
using SilverModManager.Games.Larian.Tests.BaldursGate3;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Larian.Tests.DivinityOriginalSin2DE;

/// <summary>A pak of another Larian game's format is named for the game it belongs to, and is left out of the mod list.</summary>
public class Dos2InvalidPakTests(ITestOutputHelper helper) : Dos2Fixture(helper)
{
    private static readonly DiagnosticId InvalidPakId = new("SilverModManager.Games.Larian.BaldursGate3", number: 3);

    private static byte[] Bg3Pak() => SyntheticPak.Build(18, ("Mods/Foreign/meta.lsx", SyntheticPak.MetaLsx("Foreign", "Foreign", "f6f6f6f6-0000-4000-8000-000000000006", 1)));

    private async Task<List<Diagnostic>> InvalidPakDiagnostics(Loadout.ReadOnly loadout) =>
        (await Game.DiagnosticEmitters.OfType<LarianDependencyEmitter>().Single().Diagnose(Refresh(loadout), CancellationToken.None).ToListAsync())
        .Where(d => d.Id == InvalidPakId).ToList();

    [Fact]
    public async Task AV18PakInADos2LoadoutNamesTheGameItBelongsTo()
    {
        await WriteProfile("Alice", GameEntries);
        var loadout = await CreateLoadout();
        await InstallMod(loadout, "Foreign mod", "foreign.pak", Bg3Pak());

        var diagnostic = (await InvalidPakDiagnostics(loadout)).Should().ContainSingle().Subject;

        diagnostic.FormatDetails(ServiceProvider.GetRequiredService<IDiagnosticWriter>()).Should().Contain("Baldur's Gate 3");
    }

    [Fact]
    public async Task AForeignPakIsLeftOutOfTheModList()
    {
        await WriteProfile("Alice", GameEntries);
        var loadout = await CreateLoadout();
        await InstallMod(loadout, "Foreign mod", "foreign.pak", Bg3Pak());
        await InstallMod(loadout, "Alpha mod", "alpha.pak", AlphaPak());

        await Apply(loadout);

        DiskNames("Alice").Should().Contain("Alpha").And.NotContain("Foreign");
    }

    [Fact]
    public async Task APakOfTheGamesOwnVersionIsNotReported()
    {
        await WriteProfile("Alice", GameEntries);
        var loadout = await CreateLoadout();
        await InstallMod(loadout, "Alpha mod", "alpha.pak", AlphaPak());

        (await InvalidPakDiagnostics(loadout)).Should().BeEmpty();
    }
}
