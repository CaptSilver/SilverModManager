using FluentAssertions;
using NSubstitute;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.LoadOrder;
using Xunit;

namespace SilverModManager.Collections.Tests.LoadOrder;

public class LoadOrderPageTests
{
    private static IGame GameWith(int varieties, bool? hidesLoadOrderPage)
    {
        var sortOrderManager = Substitute.For<ISortOrderManager>();
        sortOrderManager.GetSortOrderVarieties().Returns(Enumerable.Range(0, varieties).Select(_ => Substitute.For<ISortOrderVariety>()).ToArray());

        var game = hidesLoadOrderPage is { } hides
            ? Substitute.For<IGame, ILoadOrderPageVisibility>()
            : Substitute.For<IGame>();
        game.SortOrderManager.Returns(sortOrderManager);
        if (hidesLoadOrderPage is { } value) ((ILoadOrderPageVisibility)game).HidesLoadOrderPage.Returns(value);
        return game;
    }

    [Fact]
    public void ALoadOrderIsShownForAGameWithNoVarietiesAndNoOpinion()
    {
        LoadOrderPage.IsShown(GameWith(0, null)).Should().BeTrue();
        DefaultPriorityOrderSink.PriorityPage(GameWith(0, null)).Should().Be(ModPriorityOrderVariety.PageName);
    }

    [Fact]
    public void ALoadOrderIsShownForAGameThatDoesNotHideIt()
    {
        LoadOrderPage.IsShown(GameWith(0, false)).Should().BeTrue();
    }

    [Fact]
    public void ALoadOrderIsHiddenForAGameThatHidesIt()
    {
        LoadOrderPage.IsShown(GameWith(0, true)).Should().BeFalse();
    }

    [Fact]
    public void ALoadOrderIsNotShownForAGameWithItsOwnVarieties()
    {
        LoadOrderPage.IsShown(GameWith(1, null)).Should().BeFalse();
        LoadOrderPage.IsShown(GameWith(1, false)).Should().BeFalse();
    }

    [Fact]
    public void PriorityPageIsFileConflictsForAGameThatHidesTheLoadOrderPage()
    {
        DefaultPriorityOrderSink.PriorityPage(GameWith(0, true)).Should().Be("File Conflicts");
    }

    [Fact]
    public void PriorityPageIsFileConflictsForAGameWithVarieties()
    {
        DefaultPriorityOrderSink.PriorityPage(GameWith(2, null)).Should().Be("File Conflicts");
    }
}
