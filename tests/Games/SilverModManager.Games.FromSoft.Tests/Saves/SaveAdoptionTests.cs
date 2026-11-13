using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.Games.FromSoft.Saves;

namespace SilverModManager.Games.FromSoft.Tests.Saves;

public class SaveAdoptionTests : IDisposable
{
    private static readonly FromSoftSaveNames Names = new("TS0000.sl2", "TS0000_silver.sl2", null, "TS0000.sl3");

    private readonly SaveFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private AbsolutePath Root => NexusMods.Paths.FileSystem.Shared.FromUnsanitizedFullPath(_fixture.SaveFolder);

    [Fact]
    public void AdoptionFallsBackToTheSaveAnotherLoaderLeft()
    {
        _fixture.Save("TS0000.sl3", "from the other loader");

        var plan = Names.PlanAdoption("TS0000_abc.sl2", Root);

        plan.Should().NotBeNull();
        plan!.Copies.Should().Equal(new SaveAdoptionPlan.Copy(Root / "TS0000.sl3", Root / "TS0000_abc.sl2"));
        plan.Description.Should().Contain("TS0000.sl3");
    }

    [Fact]
    public void TheLegacySavesBackupComesAlongWithIt()
    {
        _fixture.Save("TS0000.sl3", "save");
        _fixture.Save("TS0000.sl3.bak", "older");

        Names.PlanAdoption("TS0000_abc.sl2", Root)!.Copies.Should().Equal(
            new SaveAdoptionPlan.Copy(Root / "TS0000.sl3", Root / "TS0000_abc.sl2"),
            new SaveAdoptionPlan.Copy(Root / "TS0000.sl3.bak", Root / "TS0000_abc.sl2.bak"));
    }

    [Fact]
    public void TheModdedSaveWinsOverTheLegacyOne()
    {
        _fixture.Save("TS0000.sl3", "legacy");
        _fixture.Save("TS0000_silver.sl2", "ours");

        var plan = Names.PlanAdoption("TS0000_abc.sl2", Root)!;

        plan.Copies.Should().ContainSingle().Which.Source.Should().Be(Root / "TS0000_silver.sl2");
        plan.Description.Should().Contain("TS0000_silver.sl2");
    }

    [Fact]
    public void NothingIsAdoptedOverASavedSlot()
    {
        _fixture.Save("TS0000.sl3", "legacy");
        _fixture.Save("TS0000_abc.sl2", "already here");

        Names.PlanAdoption("TS0000_abc.sl2", Root).Should().BeNull();
    }

    [Fact]
    public void AGameWithoutALegacyNameAdoptsOnlyTheModdedSave()
    {
        _fixture.Save("TS0000.sl3", "legacy");

        new FromSoftSaveNames("TS0000.sl2", "TS0000_silver.sl2", null).PlanAdoption("TS0000_abc.sl2", Root).Should().BeNull();
    }
}
