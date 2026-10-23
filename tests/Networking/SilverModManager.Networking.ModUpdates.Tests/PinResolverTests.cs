using FluentAssertions;
using NexusMods.Networking.ModUpdates.Tests.Helpers;
using static NexusMods.Networking.ModUpdates.Tests.Helpers.RecordedNexusFileLists;

namespace NexusMods.Networking.ModUpdates.Tests;

public class PinResolverTests
{
    private static PinChoice Resolve(RecordedFile[] files, ulong pinnedFileId, ulong modId, bool frozen = false)
    {
        var current = ById(files, pinnedFileId).ToPinFile();
        return PinResolver.Resolve(current, MainsOtherThan(files, pinnedFileId), frozen, modId);
    }

    [Fact]
    public void ASupersededPinWithOneMainRepins()
    {
        var choice = Resolve(Clevers, 34558, CleversModId);

        choice.Action.Should().Be(PinAction.Repin);
        choice.Candidate!.Value.FileId.Should().Be(49639);
    }

    [Fact]
    public void ASupersededPinAmongManyMainsRepinsToItsOwnVariant()
    {
        var choice = Resolve(MapForGoblins, 48311, MapForGoblinsModId);

        choice.Action.Should().Be(PinAction.Repin);
        choice.Candidate!.Value.FileId.Should().Be(48939);
    }

    [Fact]
    public void ASiblingVariantNeverRepins()
    {
        // The only other MAIN is NoFallDead's sibling; same version number, different mod.
        var choice = Resolve(NoFallDead, 48340, NoFallDeadModId);

        choice.Action.Should().Be(PinAction.Unchanged);
        choice.Candidate.Should().BeNull();
    }

    [Fact]
    public void ASiblingOfEveryVariantStaysPutWhenOnlyBossResurrectionLiteIsNewer()
    {
        var choice = Resolve(BossResurrection, 24924, BossResurrectionModId);

        choice.Action.Should().Be(PinAction.Unchanged);
    }

    [Fact]
    public void BetterBowsStaysOnItsRegulationDespiteAHigherVersionElsewhere()
    {
        // The page reads 1.5, but the 1.5 files are CSV spreadsheets and not MAIN.
        var choice = Resolve(BetterBows, 41004, BetterBowsModId);

        choice.Action.Should().Be(PinAction.Unchanged);
        choice.Candidate.Should().BeNull();
    }

    [Fact]
    public void AnUnmatchedVariantKeepsThePin()
    {
        var current = new PinFile(48311, "Something Removed - v2.0.5 10062 v2.0.5 2026-07-14T10-47Z AAAAAAAAA.zip", "v2.0.5");

        var choice = PinResolver.Resolve(current, MainsOtherThan(MapForGoblins, 48311), frozen: false, MapForGoblinsModId);

        choice.Action.Should().Be(PinAction.Unchanged);
        choice.Reason.Should().Contain("matched 0 of 9");
    }

    [Fact]
    public void TwoVariantMatchesKeepThePinAndNameBoth()
    {
        var files = MapForGoblins.Append(new RecordedFile(99999, "v2.1.2", "MAIN",
            "Vanilla or Randomizer - v2.1.2 10062 v2.1.2 2026-08-06T09-00Z BBBBBBBBB.zip")).ToArray();

        var choice = Resolve(files, 48311, MapForGoblinsModId);

        choice.Action.Should().Be(PinAction.Ambiguous);
        choice.Candidate.Should().BeNull();
        choice.Reason.Should().Contain("48939").And.Contain("99999");
    }

    [Fact]
    public void NoCandidatesAtAllKeepsThePin()
    {
        var files = Clevers.Select(f => f with { Category = "ARCHIVED" }).ToArray();

        var choice = Resolve(files, 34558, CleversModId);

        choice.Action.Should().Be(PinAction.Unchanged);
        choice.Candidate.Should().BeNull();
    }

    [Fact]
    public void AFrozenPinHoldsEvenWhenItWouldOtherwiseMove()
    {
        var choice = Resolve(Clevers, 34558, CleversModId, frozen: true);

        choice.Action.Should().Be(PinAction.Unchanged);
        choice.Candidate.Should().BeNull();
        choice.Reason.Should().Be("frozen");
    }
}
