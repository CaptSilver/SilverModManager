using FluentAssertions;
using NexusMods.Games.Bethesda.LoadOrder.Ordering;
using static NexusMods.Games.Bethesda.LoadOrder.Tests.Ordering.OrderBuilder;

namespace NexusMods.Games.Bethesda.LoadOrder.Tests.Ordering;

public class PluginOrderResolverTests
{
    private static IReadOnlyList<string> ResolveValid(params PluginOrderEntry[] entries)
    {
        var order = PluginOrderResolver.Resolve(entries);
        PluginOrderValidation.Validate(order, entries, EarlyNames(entries)).Should().BeNull();
        return order;
    }

    [Fact]
    public void EmptyInputGivesEmptyOrder() => PluginOrderResolver.Resolve([]).Should().BeEmpty();

    [Fact]
    public void DuplicateNamesAreRejectedCaseInsensitively()
    {
        var act = () => PluginOrderResolver.Resolve([Esp("A.esp"), Esp("a.ESP")]);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void UserOrderIsHonouredWhereMastersAllow()
    {
        // A needs two masters and B needs one; a fewest-dependencies-first sort would put B first.
        var order = ResolveValid(
            Esm("Skyrim.esm", 0),
            Esm("Update.esm", 1, masters: "Skyrim.esm"),
            Esp("A.esp", 2, "Skyrim.esm", "Update.esm"),
            Esp("B.esp", 3, "Skyrim.esm"));

        order.Should().Equal("Skyrim.esm", "Update.esm", "A.esp", "B.esp");
    }

    [Fact]
    public void AMasterThatTheUserPlacedLaterIsPulledAheadOfItsDependent()
    {
        var order = ResolveValid(Esp("A.esp", 0, "C.esp"), Esp("C.esp", 1), Esp("B.esp", 2));
        order.Should().Equal("C.esp", "A.esp", "B.esp");
    }

    [Fact]
    public void LowestIndexAmongTheAvailableIsTakenAtEachStep()
    {
        // D waits on C; after C comes out, D (index 1) beats B (index 3).
        var order = ResolveValid(Esp("C.esp", 0), Esp("D.esp", 1, "C.esp"), Esp("B.esp", 3), Esp("A.esp", 2));
        order.Should().Equal("C.esp", "D.esp", "A.esp", "B.esp");
    }

    [Fact]
    public void MasterNamesMatchIgnoringCase()
    {
        var order = ResolveValid(Esp("A.esp", 0, "B.ESP"), Esp("b.esp", 1));
        order.Should().Equal("b.esp", "A.esp");
    }

    [Fact]
    public void PluginsWithoutAnIndexComeLastSortedByName()
    {
        var order = ResolveValid(Esp("Z.esp"), Esp("m.esp", 5), Esp("a.esp"), Esp("B.esp", 9));
        order.Should().Equal("m.esp", "B.esp", "a.esp", "Z.esp");
    }

    [Fact]
    public void AMissingMasterPlacesThePluginByIndexAlone()
    {
        var order = ResolveValid(Esp("A.esp", 0, "Missing.esm"), Esp("B.esp", 1));
        order.Should().Equal("A.esp", "B.esp");
    }

    [Fact]
    public void MastersLoadBeforeNonMastersBeforeBlueprintMasters()
    {
        var order = ResolveValid(
            Blueprint("Blueprint.esm", 0),
            Esp("Plain.esp", 1),
            Esm("Base.esm", 2));

        order.Should().Equal("Base.esm", "Plain.esp", "Blueprint.esm");
    }

    [Fact]
    public void ABlueprintFlagWithoutTheMasterFlagIsAnOrdinaryPlugin()
    {
        var plugins = new[]
        {
            new PluginOrderEntry("Odd.esp", IsMaster: false, IsBlueprint: true, [], 0, null),
            Esp("Plain.esp", 1),
        };
        PluginOrderResolver.Resolve(plugins).Should().Equal("Odd.esp", "Plain.esp");
    }

    [Fact]
    public void EarlyLoadersComeFirstInEarlyIndexOrderRegardlessOfSortIndex()
    {
        var order = ResolveValid(
            Esp("User.esp", 0),
            Esm("Update.esm", 1, early: 1),
            Esm("Skyrim.esm", 2, early: 0),
            Esm("Other.esm", 3));

        order.Should().Equal("Skyrim.esm", "Update.esm", "Other.esm", "User.esp");
    }

    [Fact]
    public void AnEarlyLoaderStaysAheadOfAMasterItNamesThatIsNotEarly()
    {
        // The game loads Early.esm first no matter what, so Late.esm cannot be hoisted in front of it.
        var entries = Indexed(
            Esm("Early.esm", early: 0, masters: "Late.esm"),
            Esm("Late.esm"),
            Esp("User.esp"));

        ResolveValid(entries).Should().Equal("Early.esm", "Late.esm", "User.esp");
    }

    [Fact]
    public void ABlueprintMasterNeverLoadsEarlyEvenWhenHardcoded()
    {
        var entries = new[]
        {
            Esm("Starfield.esm", 0, early: 0),
            Esp("Plain.esp", 1),
            Blueprint("Shattered.esm", 2) with { EarlyLoaderIndex = 1 },
            Esm("Medium.esm", 3, early: 2),
        };

        var order = ResolveValid(entries);
        order.Should().Equal("Starfield.esm", "Medium.esm", "Plain.esp", "Shattered.esm");
    }

    [Fact]
    public void AMasterCycleThrows()
    {
        var act = () => PluginOrderResolver.Resolve([Esm("A.esm", 0, masters: "B.esm"), Esm("B.esm", 1, masters: "A.esm")]);
        act.Should().Throw<PluginOrderCycleException>().WithMessage("*A.esm*B.esm*");
    }

    [Fact]
    public void ACycleBehindAnIndependentPluginStillThrows()
    {
        var act = () => PluginOrderResolver.Resolve([Esp("Free.esp", 0), Esp("A.esp", 1, "B.esp"), Esp("B.esp", 2, "A.esp")]);
        act.Should().Throw<PluginOrderCycleException>();
    }

    // The three hoist_masters tests from libloadorder.

    [Fact]
    public void HoistsPluginsMastersDependOnToBeforeTheirFirstDependent()
    {
        // A master file's master that sits later moves to directly before the master file; both a master
        // and a non-master get hoisted.
        var entries = Indexed(
            Esm("Blank - Master Dependent.esm", masters: "Blank.esm"),
            Esm("Blank.esm"),
            Esm("Blank - Plugin Dependent.esm", masters: "Blank.esp"),
            Esp("Blank - Master Dependent.esp", null, "Blank.esm", "Blank - Master Dependent.esm"),
            Esp("Blank - Different.esp"),
            Esp("漢字.esp"),
            Esp("Blank.esp"));

        var order = ResolveValid(entries);

        order.Should().Equal(
            "Blank.esm",
            "Blank - Master Dependent.esm",
            "Blank.esp",
            "Blank - Plugin Dependent.esm",
            "Blank - Master Dependent.esp",
            "Blank - Different.esp",
            "漢字.esp");
    }

    [Fact]
    public void DoesNotHoistABlueprintMasterOfANonBlueprintPlugin()
    {
        var entries = Indexed(
            Esm("Blank - Override.full.esm", masters: "Blank.full.esm"),
            Esp("Blank.esp"),
            Blueprint("Blank.full.esm"));

        ResolveValid(entries).Should().Equal("Blank - Override.full.esm", "Blank.esp", "Blank.full.esm");
    }

    [Fact]
    public void HoistsABlueprintMasterOfABlueprintPlugin()
    {
        var entries = Indexed(
            Esp("Blank.esp"),
            Blueprint("Blank - Override.full.esm", masters: "Blank.full.esm"),
            Blueprint("Blank.full.esm"));

        ResolveValid(entries).Should().Equal("Blank.esp", "Blank.full.esm", "Blank - Override.full.esm");
    }

    [Fact]
    public void ANonMasterThatAMasterNeedsIsHoistedAheadOfTheEarliestMasterThatNeedsIt()
    {
        var entries = Indexed(
            Esm("First.esm", masters: "Shared.esp"),
            Esm("Second.esm", masters: "Shared.esp"),
            Esp("Shared.esp"));

        ResolveValid(entries).Should().Equal("Shared.esp", "First.esm", "Second.esm");
    }

    [Fact]
    public void ALaterHoistLandsBeforeItsMasterAfterAnEarlierHoistShiftedIt()
    {
        // Hoisting A.esp ahead of First.esm pushes Second.esm down a place, so B.esp's destination has to
        // follow it or B.esp lands before First.esm.
        var entries = Indexed(
            Esm("First.esm", masters: "A.esp"),
            Esm("Second.esm", masters: "B.esp"),
            Esp("A.esp"),
            Esp("B.esp"));

        ResolveValid(entries).Should().Equal("A.esp", "First.esm", "B.esp", "Second.esm");
    }
}
