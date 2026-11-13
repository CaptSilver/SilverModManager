using FluentAssertions;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Games.LoadOrderFile.Tests.Fixtures;

namespace SilverModManager.Games.LoadOrderFile.Tests;

public class ManifestDependencyEmitterTests(ITestOutputHelper outputHelper)
    : ARimWorldEmitterTests<ManifestDependencyEmitterTests, ManifestDependencyEmitter>(outputHelper)
{
    private static Diagnostics.MissingDependencyMessageData DataOf(Diagnostic diagnostic)
        => diagnostic.Should().BeOfType<Diagnostic<Diagnostics.MissingDependencyMessageData>>().Subject.MessageData;

    [Fact]
    public async Task AModWhoseDependencyIsNotInTheListIsReported()
    {
        var loadout = await CreateLoadout();
        var a = await InstallMod(loadout, "Mod.A", Dependencies("Mod.Lib", "Mod.Other"));
        await InstallMod(loadout, "Mod.Other");

        var diagnostic = (await Diagnose(loadout)).Should().ContainSingle().Subject;

        diagnostic.Id.Should().Be(new DiagnosticId("SilverModManager.Games.LoadOrderFile", number: 4));
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        var data = DataOf(diagnostic);
        data.Mod.Should().Be("mod.a");
        data.Missing.Should().Be("mod.lib");
        data.Group.DataId.Value.Should().Be(a.Id);
    }

    [Fact]
    public async Task SeveralMissingDependenciesAreOneDiagnosticNamingAll()
    {
        var loadout = await CreateLoadout();
        await InstallMod(loadout, "Mod.A", Dependencies("Mod.Lib", "Mod.Other"));

        DataOf((await Diagnose(loadout)).Should().ContainSingle().Subject).Missing.Should().Be("mod.lib, mod.other");
    }

    [Fact]
    public async Task ADependencyProvidedByAWorkshopItemIsSatisfied()
    {
        var loadout = await CreateLoadout();
        await InstallMod(loadout, "Mod.A", Dependencies("Workshop.Lib"));
        await SubscribeToWorkshopMod("555", "Workshop.Lib");
        var file = GameInstallation.Locations.ToAbsolutePath(Definition.OrderFile.ToGamePath());
        file.Parent.CreateDirectory();
        await file.WriteAllTextAsync(Definition.Format.Render(["workshop.lib"], null));

        (await Diagnose(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task NothingIsReportedWhenEveryDependencyIsListed()
    {
        var loadout = await CreateLoadout();
        await InstallMod(loadout, "Mod.A", Dependencies("MOD.LIB"));
        await InstallMod(loadout, "Mod.Lib");

        (await Diagnose(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task ADisabledModsDependenciesAreNotChecked()
    {
        var loadout = await CreateLoadout();
        var a = await InstallMod(loadout, "Mod.A", Dependencies("Mod.Lib"));
        await DisableItem(a.Id);

        (await Diagnose(loadout)).Should().BeEmpty();
    }
}
