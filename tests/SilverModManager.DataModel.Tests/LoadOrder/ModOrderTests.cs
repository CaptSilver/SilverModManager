using FluentAssertions;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;

namespace SilverModManager.DataModel.Tests.LoadOrder;

public class ModOrderTests
{
    [Fact]
    public void CreateRefusesAGroupListedTwice()
    {
        var g = LoadoutItemGroupId.From(EntityId.From(1));
        var act = () => ModOrder.Create([new(g, null), new(g, true)], []);
        act.Should().Throw<ArgumentException>().WithMessage("*listed twice*");
    }

    [Fact]
    public void CreateKeepsOrderAndDrops()
    {
        var a = LoadoutItemGroupId.From(EntityId.From(1));
        var b = LoadoutItemGroupId.From(EntityId.From(2));
        var order = ModOrder.Create([new(a, null), new(b, false)], [new("Ghost", "no installed mod matches it")]);
        order.WinnerToLoser.Select(e => e.Group).Should().Equal(a, b);
        order.Dropped.Should().ContainSingle().Which.Item.Should().Be("Ghost");
    }
}
