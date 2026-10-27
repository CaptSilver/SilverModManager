using FluentAssertions;
using SilverModManager.Abstractions.Loadouts;

namespace SilverModManager.Abstractions.Games.Tests.SortOrder;

public class SeedFromGroupOrderTests
{
    private static readonly LoadoutItemGroupId A = LoadoutItemGroupId.From(0x0100000000000001UL);
    private static readonly LoadoutItemGroupId B = LoadoutItemGroupId.From(0x0100000000000002UL);
    private static readonly LoadoutItemGroupId C = LoadoutItemGroupId.From(0x0100000000000003UL);

    private static (int Index, LoadoutItemGroupId? Group)[] Items(params LoadoutItemGroupId?[] groups) =>
        groups.Select((group, index) => (index, group)).ToArray();

    [Fact]
    public void GreaterIndexWinsPutsTheWinnersItemsLastWithinTheBlocksSlots()
    {
        // idx0:A idx1:B idx2:none idx3:A idx4:B, B wins: A's items take the first two slots, B's the last two.
        var result = GroupOrderSlots.SlotOrder(Items(A, B, null, A, B), [B, A], IndexOverrideBehavior.GreaterIndexWins);

        result.Should().Equal(0, 3, 2, 1, 4);
    }

    [Fact]
    public void SmallerIndexWinsPutsTheWinnersItemsFirst()
    {
        var result = GroupOrderSlots.SlotOrder(Items(A, B, null, A, B), [B, A], IndexOverrideBehavior.SmallerIndexWins);

        result.Should().Equal(3, 0, 2, 4, 1);
    }

    [Fact]
    public void ItemsOfOneGroupKeepTheirRelativeOrder()
    {
        // Slots {0,1,2,3}; A (three items) wins over B under GreaterIndexWins, so B's lone item takes slot 0.
        var result = GroupOrderSlots.SlotOrder(Items(A, A, B, A), [A, B], IndexOverrideBehavior.GreaterIndexWins);

        result.Should().Equal(1, 2, 0, 3);
    }

    [Fact]
    public void GroupsNotInTheOrderDoNotMove()
    {
        var result = GroupOrderSlots.SlotOrder(Items(C, B, C, A), [B, A], IndexOverrideBehavior.GreaterIndexWins);

        result.Should().Equal(0, 3, 2, 1);
    }

    [Fact]
    public void SparseIndexesAreKept()
    {
        // The slots are the indexes the items hold, not 0..n-1: a hidden row may sit in the gap.
        var items = new (int Index, LoadoutItemGroupId? Group)[] { (0, A), (2, B) };

        GroupOrderSlots.SlotOrder(items, [A, B], IndexOverrideBehavior.GreaterIndexWins).Should().Equal(2, 0);
    }
}
