using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Games.LoadOrderFile.Tests.Fixtures;

namespace SilverModManager.Games.LoadOrderFile.Tests;

public class WorkshopDuplicateEmitterTests(ITestOutputHelper outputHelper)
    : ARimWorldEmitterTests<WorkshopDuplicateEmitterTests, WorkshopDuplicateEmitter>(outputHelper)
{
    [Fact]
    public async Task AManagedModAlsoSubscribedToIsReported()
    {
        var loadout = await CreateLoadout();
        var harmony = await InstallMod(loadout, "Brrainz.Harmony");
        await SubscribeToWorkshopMod("2009463077", "Brrainz.Harmony");

        var diagnostic = (await Diagnose(loadout)).Should().ContainSingle().Subject;

        diagnostic.Id.Should().Be(new DiagnosticId("SilverModManager.Games.LoadOrderFile", number: 1));
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        var data = diagnostic.Should().BeOfType<Diagnostic<Diagnostics.WorkshopDuplicateMessageData>>().Subject.MessageData;
        data.Mod.Should().Be("brrainz.harmony");
        data.Group.DataId.Value.Should().Be(harmony.Id);
    }

    [Fact]
    public async Task NothingIsReportedWithoutADuplicate()
    {
        var loadout = await CreateLoadout();
        await InstallMod(loadout, "Brrainz.Harmony");
        await SubscribeToWorkshopMod("1", "Some.Other");

        (await Diagnose(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task ADisabledManagedModIsNotReported()
    {
        var loadout = await CreateLoadout();
        var harmony = await InstallMod(loadout, "Brrainz.Harmony");
        await SubscribeToWorkshopMod("2009463077", "Brrainz.Harmony");
        await DisableItem(harmony.Id);

        (await Diagnose(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task TheDetailsSayWhatToDo()
    {
        var loadout = await CreateLoadout();
        await InstallMod(loadout, "Brrainz.Harmony");
        await SubscribeToWorkshopMod("2009463077", "Brrainz.Harmony");

        var diagnostic = (await Diagnose(loadout)).Single();

        diagnostic.FormatSummary(ServiceProvider.GetRequiredService<IDiagnosticWriter>()).Should().Contain("installed both here and from the Steam Workshop");
        diagnostic.FormatDetails(ServiceProvider.GetRequiredService<IDiagnosticWriter>()).Should().Contain("Unsubscribe from the Workshop item");
    }
}
