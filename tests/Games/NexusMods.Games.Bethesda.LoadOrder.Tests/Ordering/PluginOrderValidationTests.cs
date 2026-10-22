using FluentAssertions;
using NexusMods.Games.Bethesda.LoadOrder.Ordering;
using static NexusMods.Games.Bethesda.LoadOrder.Tests.Ordering.OrderBuilder;

namespace NexusMods.Games.Bethesda.LoadOrder.Tests.Ordering;

/// <summary>
/// The validate_load_order tests from libloadorder, as hand-built lists. The error cases are inputs the
/// resolver must never produce, so each one also resolves the same entries and checks that output passes.
/// </summary>
public class PluginOrderValidationTests
{
    private static PluginOrderValidationError? Validate(string[] order, PluginOrderEntry[] entries, params string[] early) =>
        PluginOrderValidation.Validate(order, entries, early);

    private static void ResolverOutputValidates(PluginOrderEntry[] entries, params string[] early)
    {
        // Entries are given in the hand-built order, so the resolver starts from the same user arrangement.
        var indexed = Indexed(entries);
        PluginOrderValidation.Validate(PluginOrderResolver.Resolve(indexed), indexed, early).Should().BeNull();
    }

    [Fact]
    public void OnlyMasterFilesIsValid()
    {
        var entries = new[] { Esm("Blank - Different.esm"), Esm("Blank.esm") };
        Validate(Names("Blank - Different.esm", "Blank.esm"), entries).Should().BeNull();
        ResolverOutputValidates(entries);
    }

    [Fact]
    public void NoMasterFilesIsValid()
    {
        var entries = new[] { Esp("Blank.esp"), Esp("Blank - Different.esp") };
        Validate(Names("Blank.esp", "Blank - Different.esp"), entries).Should().BeNull();
        ResolverOutputValidates(entries);
    }

    [Fact]
    public void MasterFilesBeforeAllOthersIsValid()
    {
        var entries = new[] { Esm("Blank.esm"), Esp("Blank.esp") };
        Validate(Names("Blank.esm", "Blank.esp"), entries).Should().BeNull();
        ResolverOutputValidates(entries);
    }

    [Fact]
    public void HoistedNonMastersLoadingBeforeMastersIsValid()
    {
        var entries = new[] { Esm("Blank.esm"), Esp("Blank.esp"), Esm("Blank - Plugin Dependent.esm", masters: "Blank.esp") };
        Validate(Names("Blank.esm", "Blank.esp", "Blank - Plugin Dependent.esm"), entries).Should().BeNull();
        ResolverOutputValidates(entries);
    }

    [Fact]
    public void NonMastersHoistedEarlierThanNeededIsAnError()
    {
        var entries = new[] { Esp("Blank.esp"), Esm("Blank.esm"), Esm("Blank - Plugin Dependent.esm", masters: "Blank.esp") };

        var error = Validate(Names("Blank.esp", "Blank.esm", "Blank - Plugin Dependent.esm"), entries);

        error.Should().NotBeNull();
        error!.Kind.Should().Be(PluginOrderErrorKind.NonMasterBeforeMaster);
        error.Plugin.Should().Be("Blank.esm");
        error.Other.Should().Be("Blank.esp");
        ResolverOutputValidates(entries);
    }

    [Fact]
    public void MasterFileLoadingBeforeANonMasterItHasAsMasterIsAnError()
    {
        var entries = new[] { Esm("Blank.esm"), Esm("Blank - Plugin Dependent.esm", masters: "Blank.esp"), Esp("Blank.esp") };

        var error = Validate(Names("Blank.esm", "Blank - Plugin Dependent.esm", "Blank.esp"), entries);

        error.Should().NotBeNull();
        error!.Kind.Should().Be(PluginOrderErrorKind.UnrepresentedHoist);
        error.Plugin.Should().Be("Blank.esp");
        error.Other.Should().Be("Blank - Plugin Dependent.esm");
        ResolverOutputValidates(entries);
    }

    [Fact]
    public void MasterFileLoadingBeforeAnotherMasterItHasAsMasterIsAnError()
    {
        var entries = new[] { Esm("Blank - Master Dependent.esm", masters: "Blank.esm"), Esm("Blank.esm") };

        var error = Validate(Names("Blank - Master Dependent.esm", "Blank.esm"), entries);

        error.Should().NotBeNull();
        error!.Kind.Should().Be(PluginOrderErrorKind.UnrepresentedHoist);
        ResolverOutputValidates(entries);
    }

    [Fact]
    public void ABlueprintPluginAfterAllNonBlueprintPluginsIsValid()
    {
        var entries = new[] { Esm("Blank - Different.esm"), Esp("Blank.esp"), Blueprint("Blank.full.esm") };
        Validate(Names("Blank - Different.esm", "Blank.esp", "Blank.full.esm"), entries).Should().BeNull();
        ResolverOutputValidates(entries);
    }

    [Fact]
    public void AnEarlyLoaderBlueprintLoadingAfterANonEarlyLoaderIsValid()
    {
        // The blueprint master is hardcoded as an early loader but never loads early, so it counts as missing
        // from the early list and every later early loader moves up a place.
        var early = new[] { "Blank - Different.esm", "Blank.full.esm", "Blank.medium.esm" };
        var entries = new[]
        {
            Esm("Blank - Different.esm", early: 0),
            Esm("Blank.medium.esm", early: 2),
            Esp("Blank.esp"),
            Blueprint("Blank.full.esm") with { EarlyLoaderIndex = 1 },
        };

        Validate(Names("Blank - Different.esm", "Blank.medium.esm", "Blank.esp", "Blank.full.esm"), entries, early).Should().BeNull();
        ResolverOutputValidates(entries, early);
    }

    [Fact]
    public void ABlueprintPluginAfterANonBlueprintDependentIsValid()
    {
        var entries = new[]
        {
            Esm("Blank - Different.esm"),
            Esm("Blank - Override.full.esm", masters: "Blank.full.esm"),
            Esp("Blank.esp"),
            Blueprint("Blank.full.esm"),
        };

        Validate(Names("Blank - Different.esm", "Blank - Override.full.esm", "Blank.esp", "Blank.full.esm"), entries).Should().BeNull();
        ResolverOutputValidates(entries);
    }

    [Fact]
    public void ABlueprintPluginBeforeANonBlueprintPluginIsAnError()
    {
        var entries = new[] { Esm("Blank - Different.esm"), Blueprint("Blank.full.esm"), Esp("Blank.esp") };

        var error = Validate(Names("Blank - Different.esm", "Blank.full.esm", "Blank.esp"), entries);

        error.Should().NotBeNull();
        error!.Kind.Should().Be(PluginOrderErrorKind.InvalidBlueprintPluginPosition);
        error.Plugin.Should().Be("Blank.full.esm");
        error.Position.Should().Be(1);
        error.ExpectedPosition.Should().Be(2);
        ResolverOutputValidates(entries);
    }

    [Fact]
    public void ABlueprintPluginAfterABlueprintDependentIsAnError()
    {
        var entries = new[]
        {
            Esm("Blank - Different.esm"),
            Esp("Blank.esp"),
            Blueprint("Blank - Override.full.esm", masters: "Blank.full.esm"),
            Blueprint("Blank.full.esm"),
        };

        var error = Validate(Names("Blank - Different.esm", "Blank.esp", "Blank - Override.full.esm", "Blank.full.esm"), entries);

        error.Should().NotBeNull();
        error!.Kind.Should().Be(PluginOrderErrorKind.UnrepresentedHoist);
        error.Plugin.Should().Be("Blank.full.esm");
        error.Other.Should().Be("Blank - Override.full.esm");
        ResolverOutputValidates(entries);
    }

    [Fact]
    public void AnEarlyLoaderOutOfPlaceIsAnError()
    {
        var entries = new[] { Esm("Skyrim.esm", early: 0), Esm("Update.esm", early: 1) };

        var error = Validate(Names("Update.esm", "Skyrim.esm"), entries, "Skyrim.esm", "Update.esm");

        error.Should().NotBeNull();
        error!.Kind.Should().Be(PluginOrderErrorKind.InvalidEarlyLoadingPluginPosition);
        error.Plugin.Should().Be("Skyrim.esm");
        error.Position.Should().Be(1);
        error.ExpectedPosition.Should().Be(0);
    }

    [Fact]
    public void MissingEarlyLoadersShiftTheExpectedPositions()
    {
        var entries = new[] { Esm("Update.esm", early: 1), Esp("A.esp") };
        Validate(Names("Update.esm", "A.esp"), entries, "Skyrim.esm", "Update.esm").Should().BeNull();
    }

    [Fact]
    public void AnOrderNamingAPluginWithoutAnEntryThrows()
    {
        var act = () => Validate(Names("Ghost.esp"), [Esp("A.esp")]);
        act.Should().Throw<ArgumentException>();
    }
}
