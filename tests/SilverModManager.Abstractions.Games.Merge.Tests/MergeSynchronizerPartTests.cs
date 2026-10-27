using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Games.Merge.Tests;

public class MergeSynchronizerPartTests
{
    private static readonly GamePath OutA = new(LocationId.Game, "Mods/merged/scripts/a.txt");
    private static readonly GamePath OutB = new(LocationId.Game, "Mods/merged/scripts/b.txt");

    private static readonly PlannedOutput[] Planned = [new(OutA, "scripts/a.txt"), new(OutB, "scripts/b.txt")];

    private static IMergeEngine Engine()
    {
        var engine = Substitute.For<IMergeEngine>();
        engine.PlannedOutputs(Arg.Any<Loadout.ReadOnly>()).Returns(Planned);
        engine.Plan(Arg.Any<Loadout.ReadOnly>(), Arg.Any<MergeSourceMode>()).Returns(
        [
            new MergedOutput(OutA, "scripts/a.txt", [1], ["A", "B"], [], "text-3way"),
            new MergedOutput(OutB, "scripts/b.txt", [2], ["A", "B"], [], "text-3way"),
        ]);
        return engine;
    }

    private static Dictionary<GamePath, SyncNode> Tree(LoadoutSourceItemType type, params GamePath[] paths)
        => paths.ToDictionary(p => p, _ => new SyncNode { SourceItemType = type });

    [Fact]
    public void OneIntrinsicFilePerPlannedOutputAtItsOutputPath()
    {
        var files = new MergeSynchronizerPart(Engine()).IntrinsicFiles(default, Substitute.For<ILogger>());

        files.Keys.Should().BeEquivalentTo([OutA, OutB]);
        files.Values.Should().AllBeOfType<MergedIntrinsicFile>();
        files.Should().AllSatisfy(pair => ((MergedIntrinsicFile)pair.Value).Path.Should().Be(pair.Key));
        ((MergedIntrinsicFile)files[OutA]).RelPath.Should().Be("scripts/a.txt");
        ((MergedIntrinsicFile)files[OutB]).RelPath.Should().Be("scripts/b.txt");
    }

    [Fact]
    public void ListingReadsNoBytes()
    {
        var engine = Engine();

        new MergeSynchronizerPart(engine).IntrinsicFiles(default, Substitute.For<ILogger>());

        engine.DidNotReceiveWithAnyArgs().Plan(default, default);
        engine.Received(1).PlannedOutputs(Arg.Any<Loadout.ReadOnly>());
    }

    [Fact]
    public void AListingFailureListsNoFilesInsteadOfThrowing()
    {
        var engine = Substitute.For<IMergeEngine>();
        engine.PlannedOutputs(Arg.Any<Loadout.ReadOnly>()).Returns(_ => throw new InvalidOperationException("rows unreadable"));
        var logger = Substitute.For<ILogger>();

        var files = new MergeSynchronizerPart(engine).IntrinsicFiles(default, logger);

        files.Should().BeEmpty();
        logger.ReceivedWithAnyArgs().Log(LogLevel.Error, default, default(object), default, default!);
    }

    [Fact]
    public void CheckOutputsPassesWhenEveryOutputIsIntrinsicInTheTree()
    {
        var act = () => new MergeSynchronizerPart(Engine()).CheckOutputs(Tree(LoadoutSourceItemType.Intrinsic, OutA, OutB), default);

        act.Should().NotThrow();
    }

    [Fact]
    public void CheckOutputsRefusesAnOutputMissingFromTheTree()
    {
        var act = () => new MergeSynchronizerPart(Engine()).CheckOutputs(Tree(LoadoutSourceItemType.Intrinsic, OutA), default);

        act.Should().Throw<MergeOutputMissingException>().WithMessage("*scripts/b.txt*");
    }

    [Fact]
    public void CheckOutputsRefusesAnOutputTheTreeHoldsAsALoadoutFile()
    {
        var tree = Tree(LoadoutSourceItemType.Intrinsic, OutA);
        tree[OutB] = new SyncNode { SourceItemType = LoadoutSourceItemType.Loadout };

        var act = () => new MergeSynchronizerPart(Engine()).CheckOutputs(tree, default);

        act.Should().Throw<MergeOutputMissingException>().WithMessage("*scripts/b.txt*");
    }

    [Fact]
    public void CheckOutputsPlansInApplyMode()
    {
        var engine = Engine();

        new MergeSynchronizerPart(engine).CheckOutputs(Tree(LoadoutSourceItemType.Intrinsic, OutA, OutB), default);

        engine.Received(1).Plan(Arg.Any<Loadout.ReadOnly>(), MergeSourceMode.Apply);
    }

    [Fact]
    public void PlanningAheadPlansInApplyMode()
    {
        var engine = Engine();

        new MergeSynchronizerPart(engine).PlanOutputs(default);

        engine.Received(1).Plan(Arg.Any<Loadout.ReadOnly>(), MergeSourceMode.Apply);
    }

    [Fact]
    public void AMissingOutputIsAReadablePreflightRefusal()
    {
        new MergeOutputMissingException("scripts/b.txt").Should().BeAssignableTo<SynchronizerPreflightException>();
    }
}
