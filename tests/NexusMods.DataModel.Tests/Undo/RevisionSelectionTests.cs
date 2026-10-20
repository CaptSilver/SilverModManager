using FluentAssertions;
using NexusMods.DataModel.Undo;
using NexusMods.MnemonicDB.Abstractions;

namespace NexusMods.DataModel.Tests.Undo;

public class RevisionSelectionTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static LoadoutRevisionWithStats Revision(ulong tx, int minutes) =>
        new(new LoadoutRevision(EntityId.From(1), EntityId.From(tx), Start.AddMinutes(minutes)), ModCount: 0);

    [Fact]
    public void NumbersCountUpFromTheOldestRevision()
    {
        // Deliberately not in timestamp order: the numbering follows the timestamp, as the revisions verb does.
        var revisions = new[] { Revision(tx: 30, minutes: 3), Revision(tx: 10, minutes: 1), Revision(tx: 20, minutes: 2) };

        RevisionSelection.TryGet(revisions, 0, out var first).Should().BeTrue();
        first.TxEntity.Should().Be(EntityId.From(10));
        RevisionSelection.TryGet(revisions, 2, out var last).Should().BeTrue();
        last.TxEntity.Should().Be(EntityId.From(30));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public void NumbersOutsideTheListAreRejected(int number)
    {
        var revisions = new[] { Revision(10, 1), Revision(20, 2), Revision(30, 3) };

        RevisionSelection.TryGet(revisions, number, out var revision).Should().BeFalse();
        revision.Should().Be(default(LoadoutRevision));
    }

    [Fact]
    public void AnEmptyListRejectsEveryNumber()
    {
        RevisionSelection.TryGet([], 0, out _).Should().BeFalse();
    }
}
