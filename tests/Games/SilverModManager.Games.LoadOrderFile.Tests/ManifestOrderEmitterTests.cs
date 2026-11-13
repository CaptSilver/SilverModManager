using FluentAssertions;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Games.LoadOrderFile.Tests.Fixtures;

namespace SilverModManager.Games.LoadOrderFile.Tests;

public class ManifestOrderEmitterTests(ITestOutputHelper outputHelper)
    : ARimWorldEmitterTests<ManifestOrderEmitterTests, ManifestOrderEmitter>(outputHelper)
{
    private static DiagnosticId Violation => new("SilverModManager.Games.LoadOrderFile", number: 2);

    private static Diagnostics.OrderViolationMessageData DataOf(Diagnostic diagnostic)
        => diagnostic.Should().BeOfType<Diagnostic<Diagnostics.OrderViolationMessageData>>().Subject.MessageData;

    [Fact]
    public async Task AManagedModBelowTheOneItMustLoadAfterIsReported()
    {
        var loadout = await CreateLoadout();
        var a = await InstallMod(loadout, "Mod.A", Rule("loadAfter", "Mod.B"));
        var b = await InstallMod(loadout, "Mod.B");
        await Prioritise(loadout, b, a);

        var diagnostic = (await Diagnose(loadout)).Should().ContainSingle().Subject;

        diagnostic.Id.Should().Be(Violation);
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        var data = DataOf(diagnostic);
        data.Mod.Should().Be("mod.a");
        data.Target.Should().Be("mod.b");
        data.Rule.Should().Be("load after");
        data.FixPage.Should().Be(DefaultPriorityOrderSink.PriorityPage(Game)).And.Be("Load Order");
        data.Group.DataId.Value.Should().Be(a.Id);
    }

    [Fact]
    public async Task AManagedModAboveTheOneItMustLoadBeforeIsReported()
    {
        var loadout = await CreateLoadout();
        var a = await InstallMod(loadout, "Mod.A", Rule("loadBefore", "Mod.B"));
        var b = await InstallMod(loadout, "Mod.B");
        await Prioritise(loadout, a, b);

        var data = DataOf((await Diagnose(loadout)).Should().ContainSingle().Subject);

        data.Rule.Should().Be("load before");
        data.Target.Should().Be("mod.b");
    }

    [Fact]
    public async Task AForcedRuleIsCritical()
    {
        var loadout = await CreateLoadout();
        var a = await InstallMod(loadout, "Mod.A", Rule("forceLoadAfter", "Mod.B"));
        var b = await InstallMod(loadout, "Mod.B");
        await Prioritise(loadout, b, a);

        (await Diagnose(loadout)).Should().ContainSingle().Which.Severity.Should().Be(DiagnosticSeverity.Critical);
    }

    [Fact]
    public async Task AWorkshopModThatMustLoadAfterAManagedOneSaysTheGamesListCannotFixIt()
    {
        var loadout = await CreateLoadout();
        var lib = await InstallMod(loadout, "Mod.Lib");
        await SubscribeToWorkshopMod("555", "Workshop.Thing", Rule("loadAfter", "Mod.Lib"));
        await ConfigListing("workshop.thing");

        var data = DataOf((await Diagnose(loadout)).Should().ContainSingle().Subject);

        // The app writes every mod it does not manage ahead of its own, so moving the Workshop mod in the game's list
        // would be undone by the next apply.
        data.FixPage.Should().Contain("ahead of its own").And.Contain("install workshop.thing through the app")
            .And.Contain("Load Order").And.Contain("555").And.NotContain("move workshop.thing");
        data.Group.DataId.Value.Should().Be(lib.Id);
    }

    [Fact]
    public async Task AWorkshopModOnTheWrongSideOfAHoistedModPointsAtTheGamesOwnList()
    {
        var core = GameFolder.Combine("Data/Core/About/About.xml");
        core.Parent.CreateDirectory();
        await core.WriteAllTextAsync(About("Ludeon.RimWorld"));
        var loadout = await CreateLoadout();
        await InstallMod(loadout, "Brrainz.Harmony", Rule("loadBefore", "Ludeon.RimWorld"));
        await SubscribeToWorkshopMod("777", "Workshop.Thing", Rule("loadAfter", "Brrainz.Harmony"));
        await ConfigListing("workshop.thing", "ludeon.rimworld");

        var data = DataOf((await Diagnose(loadout)).Should().ContainSingle().Subject);

        // Harmony goes directly before Core, so the Workshop mod moved below Core in the game's list stays there.
        data.FixPage.Should().Contain("move workshop.thing").And.Contain("RimWorld's mod list").And.Contain("777");
    }

    [Fact]
    public async Task NothingIsReportedWhenTheOrderSatisfiesEveryRule()
    {
        var loadout = await CreateLoadout();
        var a = await InstallMod(loadout, "Mod.A", Rule("loadAfter", "Mod.B"));
        var b = await InstallMod(loadout, "Mod.B");
        await Prioritise(loadout, a, b);

        (await Diagnose(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task AManagedModTheOrderFilePlacesBeforeItsExternalIsNotReported()
    {
        var core = GameFolder.Combine("Data/Core/About/About.xml");
        core.Parent.CreateDirectory();
        await core.WriteAllTextAsync(About("Ludeon.RimWorld"));
        var loadout = await CreateLoadout();
        await InstallMod(loadout, "Brrainz.Harmony", Rule("loadBefore", "Ludeon.RimWorld"));

        (await Diagnose(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task TwoEnabledIncompatibleModsAreReportedOnce()
    {
        var loadout = await CreateLoadout();
        await InstallMod(loadout, "Mod.A", Rule("incompatibleWith", "Mod.B"));
        await InstallMod(loadout, "Mod.B", Rule("incompatibleWith", "Mod.A"));

        var diagnostic = (await Diagnose(loadout)).Should().ContainSingle().Subject;

        diagnostic.Id.Should().Be(new DiagnosticId("SilverModManager.Games.LoadOrderFile", number: 3));
        diagnostic.Should().BeOfType<Diagnostic<Diagnostics.IncompatibleMessageData>>();
    }

    [Fact]
    public async Task ADisabledIncompatibleModIsNotReported()
    {
        var loadout = await CreateLoadout();
        await InstallMod(loadout, "Mod.A", Rule("incompatibleWith", "Mod.B"));
        var b = await InstallMod(loadout, "Mod.B");
        await DisableItem(b.Id);

        (await Diagnose(loadout)).Should().BeEmpty();
    }

    private async Task ConfigListing(params string[] names)
    {
        var file = OrderFileOnDisk;
        file.Parent.CreateDirectory();
        await file.WriteAllTextAsync(Definition.Format.Render(names, null));
    }

    private NexusMods.Paths.AbsolutePath OrderFileOnDisk => GameInstallation.Locations.ToAbsolutePath(Definition.OrderFile.ToGamePath());
}
