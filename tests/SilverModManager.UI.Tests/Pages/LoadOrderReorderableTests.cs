using System.ComponentModel;
using FluentAssertions;
using NSubstitute;
using SilverModManager.Abstractions.Games;
using SilverModManager.App.UI.Pages.Sorting;
using SilverModManager.Abstractions.Loadouts;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>Moving a load order row by a number of places: one call to the variety, in the page's own displayed order.</summary>
public class LoadOrderReorderableTests
{
    private static readonly SortOrderId SortOrder = SortOrderId.From(42UL);

    private static IReactiveSortItem Row(string key, int index)
    {
        var row = Substitute.For<IReactiveSortItem>();
        row.Key.Returns(new SortItemKey<string>(key));
        row.SortIndex.Returns(index);
        return row;
    }

    private static ISortItemKey Key(string key) => new SortItemKey<string>(key);

    /// <summary>A variety holding <paramref name="count"/> rows "0".."n" whose <c>MoveItemDelta</c> moves the row and stops at the ends, like the real ones.</summary>
    private static (ISortOrderVariety Variety, List<(ISortItemKey Key, int Delta)> Calls) Variety(int count)
    {
        var order = Enumerable.Range(0, count).Select(i => i.ToString()).ToList();
        var calls = new List<(ISortItemKey, int)>();
        var variety = Substitute.For<ISortOrderVariety>();
        variety.GetSortOrderItems(SortOrder, Arg.Any<SilverModManager.MnemonicDB.Abstractions.IDb?>())
            .Returns(_ => order.Select((key, i) => Row(key, i)).ToList());
        variety.MoveItemDelta(SortOrder, Arg.Any<ISortItemKey>(), Arg.Any<int>(), Arg.Any<SilverModManager.MnemonicDB.Abstractions.IDb?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var key = (SortItemKey<string>)call.ArgAt<ISortItemKey>(1);
                var delta = call.ArgAt<int>(2);
                calls.Add((key, delta));
                var from = order.IndexOf(key.Key);
                var to = Math.Clamp(from + delta, 0, order.Count - 1);
                order.RemoveAt(from);
                order.Insert(to, key.Key);
                return Task.CompletedTask;
            });
        return (variety, calls);
    }

    private static LoadOrderReorderable Reorderable(ISortOrderVariety variety, ListSortDirection direction = ListSortDirection.Ascending)
        => new(variety, SortOrder, () => direction);

    [Fact]
    public async Task MoveByThreeOnTheLoadOrderIsOneMoveItemDeltaCall()
    {
        var (variety, calls) = Variety(6);

        await Reorderable(variety).MoveBy(Key("1"), 3, CancellationToken.None);

        calls.Should().Equal((Key("1"), 3));
    }

    [Fact]
    public async Task MovePastTheEndLandsAtTheBottom()
    {
        var (variety, _) = Variety(5);
        var reorderable = Reorderable(variety);

        await reorderable.MoveBy(Key("2"), 10, CancellationToken.None);

        reorderable.IndexOf(Key("2")).Should().Be(4);
    }

    [Fact]
    public async Task OnADescendingPageMoveByPlusOneIsMoveItemDeltaMinusOne()
    {
        var (variety, calls) = Variety(5);

        await Reorderable(variety, ListSortDirection.Descending).MoveBy(Key("2"), +1, CancellationToken.None);

        calls.Should().Equal((Key("2"), -1));
    }

    [Fact]
    public void OnADescendingPageTheLastStoredRowIsDisplayedFirst()
    {
        var (variety, _) = Variety(5);

        var reorderable = Reorderable(variety, ListSortDirection.Descending);

        reorderable.IndexOf(Key("4")).Should().Be(0);
        reorderable.IndexOf(Key("0")).Should().Be(4);
        reorderable.CountFor(Key("0")).Should().Be(5);
    }

    [Fact]
    public async Task AMoveTheVarietyRefusesReachesTheCaller()
    {
        var (variety, _) = Variety(3);
        variety.MoveItemDelta(SortOrder, Arg.Any<ISortItemKey>(), Arg.Any<int>(), Arg.Any<SilverModManager.MnemonicDB.Abstractions.IDb?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("not stored")));

        var move = () => Reorderable(variety).MoveBy(Key("1"), 1, CancellationToken.None);

        await move.Should().ThrowAsync<InvalidOperationException>().WithMessage("not stored");
    }
}
