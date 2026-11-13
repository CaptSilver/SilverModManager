using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.App.UI.DiagnosticSystem;
using SilverModManager.Games.Larian.Engine.Emitters;
using SilverModManager.Games.Larian.Tests.BaldursGate3.Modsettings;

namespace SilverModManager.Games.Larian.Tests.BaldursGate3;

/// <summary>A pak in another Larian game's format is named for that game, for Baldur's Gate 3 as for the others.</summary>
public class BG3ForeignPakTests(ITestOutputHelper helper) : ModsettingsFixture(helper)
{
    [Fact]
    public async Task ADivinityPakInABaldursGate3LoadoutNamesDivinity()
    {
        await WriteModsettings(BaseEntries);
        var loadout = await CreateLoadout();
        await InstallMod(loadout, "Divinity mod", "divinity.pak",
            SyntheticPak.Build(13, ("Mods/Old/meta.lsx", SyntheticPak.MetaLsx("Old", "Old", "e5e5e5e5-0000-4000-8000-000000000005", 1))));

        var diagnostics = await Game.DiagnosticEmitters.OfType<LarianDependencyEmitter>().Single().Diagnose(Refresh(loadout), CancellationToken.None).ToListAsync();

        var invalid = diagnostics.Where(d => d.Id == new DiagnosticId("SilverModManager.Games.Larian.BaldursGate3", number: 3)).Should().ContainSingle().Subject;
        invalid.FormatDetails(ServiceProvider.GetRequiredService<IDiagnosticWriter>()).Should().Contain("Divinity: Original Sin 2");
    }
}
