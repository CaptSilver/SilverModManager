using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Saves;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using SilverModManager.Games.FromSoft.Tests.Saves;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FromSoft.Tests;

public class EldenRingPerLoadoutSavesTests(ITestOutputHelper helper) : AModdedEldenRingTest<EldenRingPerLoadoutSavesTests>(helper)
{
    private IPerLoadoutSaves Saves => (IPerLoadoutSaves)Game;

    private async Task<Loadout.ReadOnly> KeepSaves(Loadout.ReadOnly loadout)
    {
        using var tx = Connection.BeginTransaction();
        tx.Add(loadout.Id, Loadout.KeepSavesWithLoadout, true);
        await tx.Commit();
        return Refresh(loadout);
    }

    [Fact]
    public async Task ModdedForNamesTheSaveAfterTheLoadoutIdAndLeavesTheFixedNameAlone()
    {
        var loadout = await CreateLoadout();

        EldenRingGame.Definition.SaveNames.ModdedFor(loadout).Should().Be($"ER0000_{LoadoutSaveSlot.IdOf(loadout)}.sl2");
        EldenRingGame.Definition.SaveNames.Modded.Should().Be("ER0000_silver.sl2");
    }

    [Fact]
    public async Task TheScannerKnowsTheSaveNamedForALoadout()
    {
        var loadout = await CreateLoadout();
        var name = EldenRingGame.Definition.SaveNames.ModdedFor(loadout)!;

        new SaveFolderScanner(EldenRingGame.Definition.SaveNames).KindOf(name, out _).Should().Be(SaveKind.Modded);
    }

    [Fact]
    public async Task ThePlanRedirectsTheProfileToTheLoadoutsSaveWhenTheLoadoutKeepsItsSaves()
    {
        var loadout = await KeepSaves(await CreateLoadout());

        Saves.PlanSaveRedirect(loadout).Should().Be(new SaveRedirectPlan.LoaderKey("savefile", $"ER0000_{LoadoutSaveSlot.IdOf(loadout)}.sl2"));
    }

    [Fact]
    public async Task ThePlanIsUnsupportedForALoadoutWithSeamlessCoop()
    {
        var loadout = await CreateModdedLoadout(seamlessCoop: true);

        var plan = Saves.PlanSaveRedirect(loadout).Should().BeOfType<SaveRedirectPlan.Unsupported>().Subject;

        plan.Reason.Should().Contain("Seamless Co-op").And.Contain("ER0000.co2");
    }

    [Fact]
    public async Task AdoptingCopiesTheModdedSaveAndItsBackupToTheLoadoutsName()
    {
        var loadout = await CreateLoadout();
        using var folder = new SaveFixture();
        var root = NexusMods.Paths.FileSystem.Shared.FromUnsanitizedFullPath(folder.SaveFolder);
        folder.Save("ER0000_silver.sl2", "mine");
        folder.Save("ER0000_silver.sl2.bak", "older");
        var id = LoadoutSaveSlot.IdOf(loadout);

        var plan = Saves.PlanAdoptCurrentSave(loadout, root);

        plan.Should().NotBeNull();
        plan!.Copies.Should().Equal(
            new SaveAdoptionPlan.Copy(root / "ER0000_silver.sl2", root / $"ER0000_{id}.sl2"),
            new SaveAdoptionPlan.Copy(root / "ER0000_silver.sl2.bak", root / $"ER0000_{id}.sl2.bak"));
        plan.Description.Should().NotBeNullOrWhiteSpace();
        SaveFixture.Tree(folder.SaveFolder).Should().Equal("ER0000_silver.sl2", "ER0000_silver.sl2.bak");
    }

    [Fact]
    public async Task AdoptingOnlyCopiesTheBackupThatExists()
    {
        var loadout = await CreateLoadout();
        using var folder = new SaveFixture();
        var root = NexusMods.Paths.FileSystem.Shared.FromUnsanitizedFullPath(folder.SaveFolder);
        folder.Save("ER0000_silver.sl2", "mine");

        Saves.PlanAdoptCurrentSave(loadout, root)!.Copies.Should().ContainSingle();
    }

    [Fact]
    public async Task NothingIsAdoptedWithoutASourceOrOverADestination()
    {
        var loadout = await CreateLoadout();
        using var folder = new SaveFixture();
        var root = NexusMods.Paths.FileSystem.Shared.FromUnsanitizedFullPath(folder.SaveFolder);

        Saves.PlanAdoptCurrentSave(loadout, root).Should().BeNull("there is no save to bring in");

        folder.Save("ER0000_silver.sl2", "mine");
        folder.Save($"ER0000_{LoadoutSaveSlot.IdOf(loadout)}.sl2", "already here");
        Saves.PlanAdoptCurrentSave(loadout, root).Should().BeNull("the slot already has a save and is never overwritten");
    }
}
