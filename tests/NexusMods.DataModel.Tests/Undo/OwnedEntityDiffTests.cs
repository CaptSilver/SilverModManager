using FluentAssertions;
using NexusMods.DataModel.Undo;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.DataModel.Tests.Undo;

/// <summary>
/// The check that runs after a revert commits. It has to catch an entity whose state is not exactly the
/// revision's, including a stale value left next to the restored one.
/// </summary>
/// <remarks>
/// <see cref="Loadout.LocatorIds"/> is used only because it is multi-valued, which is the easiest way to put
/// two values for one attribute on an entity.
/// </remarks>
public class OwnedEntityDiffTests(ITestOutputHelper helper) : ANexusCollectionTest<OwnedEntityDiffTests>(helper)
{
    [Fact]
    public async Task AStaleValueNextToTheWantedOneIsAMismatch()
    {
        var (current, wanted) = await TwoEntities(["old", "new"], ["new"]);

        OwnedEntityDiff.Mismatches(current, wanted).Should().Equal([LocatorIdsAttribute]);
    }

    [Fact]
    public async Task AMissingValueIsAMismatch()
    {
        var (current, wanted) = await TwoEntities(["new"], ["old", "new"]);

        OwnedEntityDiff.Mismatches(current, wanted).Should().Equal([LocatorIdsAttribute]);
    }

    [Fact]
    public async Task ADifferentValueIsAMismatch()
    {
        var (current, wanted) = await TwoEntities(["old"], ["new"]);

        OwnedEntityDiff.Mismatches(current, wanted).Should().Equal([LocatorIdsAttribute]);
    }

    [Fact]
    public async Task TheSameValuesAreNoMismatch()
    {
        var (current, wanted) = await TwoEntities(["a", "b"], ["b", "a"]);

        OwnedEntityDiff.Mismatches(current, wanted).Should().BeEmpty();
    }

    private AttributeId LocatorIdsAttribute => Connection.AttributeCache.GetAttributeId(Loadout.LocatorIds.Id);

    private async Task<(NexusMods.MnemonicDB.Abstractions.IndexSegments.EntitySegment Current, NexusMods.MnemonicDB.Abstractions.IndexSegments.EntitySegment Wanted)> TwoEntities(
        string[] currentValues, string[] wantedValues)
    {
        using var tx = Connection.BeginTransaction();
        var current = tx.TempId();
        var wanted = tx.TempId();
        foreach (var value in currentValues)
            tx.Add(current, Loadout.LocatorIds, LocatorId.From(value));
        foreach (var value in wantedValues)
            tx.Add(wanted, Loadout.LocatorIds, LocatorId.From(value));
        var result = await tx.Commit();

        return (result.Db.Datoms(result[current]), result.Db.Datoms(result[wanted]));
    }
}
