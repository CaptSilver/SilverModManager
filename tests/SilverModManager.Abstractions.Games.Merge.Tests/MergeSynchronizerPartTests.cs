using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Games;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Games.Merge.Tests;

public class MergeSynchronizerPartTests(IServiceProvider provider) : AGameTest<StubbedGame>(provider)
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
    public async Task OneIntrinsicFilePerPlannedOutputAtItsOutputPath()
    {
        var loadout = await CreateLoadout();
        var files = new MergeSynchronizerPart(Engine()).IntrinsicFiles(loadout, Substitute.For<ILogger>());

        files.Keys.Should().BeEquivalentTo([OutA, OutB]);
        files.Values.Should().AllBeOfType<MergedIntrinsicFile>();
        files.Should().AllSatisfy(pair => ((MergedIntrinsicFile)pair.Value).Path.Should().Be(pair.Key));
        ((MergedIntrinsicFile)files[OutA]).RelPath.Should().Be("scripts/a.txt");
        ((MergedIntrinsicFile)files[OutB]).RelPath.Should().Be("scripts/b.txt");
    }

    [Fact]
    public async Task ListingReadsNoBytes()
    {
        var loadout = await CreateLoadout();
        var engine = Engine();

        new MergeSynchronizerPart(engine).IntrinsicFiles(loadout, Substitute.For<ILogger>());

        engine.DidNotReceiveWithAnyArgs().Plan(default, default);
        engine.Received(1).PlannedOutputs(Arg.Any<Loadout.ReadOnly>());
    }

    [Fact]
    public async Task AListingFailureListsNoFilesInsteadOfThrowing()
    {
        var loadout = await CreateLoadout();
        var engine = Substitute.For<IMergeEngine>();
        engine.PlannedOutputs(Arg.Any<Loadout.ReadOnly>()).Returns(_ => throw new InvalidOperationException("rows unreadable"));
        var logger = Substitute.For<ILogger>();

        var files = new MergeSynchronizerPart(engine).IntrinsicFiles(loadout, logger);

        files.Should().BeEmpty();
        logger.ReceivedWithAnyArgs().Log(LogLevel.Error, default, default(object), default, default!);
    }

    [Fact]
    public async Task CheckOutputsPassesWhenEveryOutputIsIntrinsicInTheTree()
    {
        var loadout = await CreateLoadout();
        var part = new MergeSynchronizerPart(Engine());
        part.IntrinsicFiles(loadout, Substitute.For<ILogger>());
        var act = () => part.CheckOutputs(Tree(LoadoutSourceItemType.Intrinsic, OutA, OutB), loadout);

        act.Should().NotThrow();
    }

    [Fact]
    public async Task CheckOutputsRefusesAnOutputMissingFromTheTree()
    {
        var loadout = await CreateLoadout();
        var part = new MergeSynchronizerPart(Engine());
        part.IntrinsicFiles(loadout, Substitute.For<ILogger>());
        var act = () => part.CheckOutputs(Tree(LoadoutSourceItemType.Intrinsic, OutA), loadout);

        act.Should().Throw<MergeOutputMissingException>().WithMessage("*scripts/b.txt*");
    }

    [Fact]
    public async Task CheckOutputsRefusesAnOutputTheTreeHoldsAsALoadoutFile()
    {
        var loadout = await CreateLoadout();
        var tree = Tree(LoadoutSourceItemType.Intrinsic, OutA);
        tree[OutB] = new SyncNode { SourceItemType = LoadoutSourceItemType.Loadout };

        var part = new MergeSynchronizerPart(Engine());
        part.IntrinsicFiles(loadout, Substitute.For<ILogger>());
        var act = () => part.CheckOutputs(tree, loadout);

        act.Should().Throw<MergeOutputMissingException>().WithMessage("*scripts/b.txt*");
    }

    [Fact]
    public async Task CheckOutputsPlansInApplyMode()
    {
        var loadout = await CreateLoadout();
        var engine = Engine();

        var part = new MergeSynchronizerPart(engine);
        part.IntrinsicFiles(loadout, Substitute.For<ILogger>());
        part.CheckOutputs(Tree(LoadoutSourceItemType.Intrinsic, OutA, OutB), loadout);

        engine.Received(1).Plan(Arg.Any<Loadout.ReadOnly>(), MergeSourceMode.Apply);
    }

    [Fact]
    public async Task PlanningAheadPlansInApplyMode()
    {
        var loadout = await CreateLoadout();
        var engine = Engine();

        new MergeSynchronizerPart(engine).PlanOutputs(loadout);

        engine.Received(1).Plan(Arg.Any<Loadout.ReadOnly>(), MergeSourceMode.Apply);
    }

    [Fact]
    public void AMissingOutputIsAReadablePreflightRefusal()
    {
        new MergeOutputMissingException("scripts/b.txt").Should().BeAssignableTo<SynchronizerPreflightException>();
    }

    [Fact]
    public async Task AFileTheTreeHoldsThatThePlanNoLongerHasIsRefusedAsAChangedLoadout()
    {
        var loadout = await CreateLoadout();
        var engine = Engine();
        var part = new MergeSynchronizerPart(engine);
        part.IntrinsicFiles(loadout, Substitute.For<ILogger>());
        // The merge of b.txt failed after the listing was made.
        engine.Plan(Arg.Any<Loadout.ReadOnly>(), Arg.Any<MergeSourceMode>()).Returns([new MergedOutput(OutA, "scripts/a.txt", [1], ["A", "B"], [], "text-3way")]);

        var act = () => part.CheckOutputs(Tree(LoadoutSourceItemType.Intrinsic, OutA, OutB), loadout);

        act.Should().Throw<SynchronizerPreflightException>().WithMessage("*changed while it was being applied*");
    }

    [Fact]
    public async Task AFileThePlanHasThatTheListingLeftOutIsRefusedAsAChangedLoadout()
    {
        var loadout = await CreateLoadout();
        var engine = Engine();
        engine.PlannedOutputs(Arg.Any<Loadout.ReadOnly>()).Returns([Planned[0]]);
        var part = new MergeSynchronizerPart(engine);
        part.IntrinsicFiles(loadout, Substitute.For<ILogger>());

        var act = () => part.CheckOutputs(Tree(LoadoutSourceItemType.Intrinsic, OutA), loadout);

        act.Should().Throw<SynchronizerPreflightException>().WithMessage("*changed while it was being applied*");
    }

    [Fact]
    public async Task AListingFailureRefusesTheApplyOfThatRevisionWithItsReason()
    {
        var loadout = await CreateLoadout();
        var engine = Engine();
        engine.PlannedOutputs(Arg.Any<Loadout.ReadOnly>()).Returns(_ => throw new InvalidOperationException("rows unreadable"));
        var part = new MergeSynchronizerPart(engine);
        part.IntrinsicFiles(loadout, Substitute.For<ILogger>());

        var act = () => part.CheckOutputs([], loadout);

        act.Should().Throw<SynchronizerPreflightException>().WithMessage("*rows unreadable*");
    }

    [Fact]
    public async Task AStrategyRefusingItsInputDuringPlanningIsAReadablePreflightRefusal()
    {
        var loadout = await CreateLoadout();
        var engine = Engine();
        engine.Plan(Arg.Any<Loadout.ReadOnly>(), Arg.Any<MergeSourceMode>()).Returns(_ => throw new MergeInputException("A's x.txt is not UTF-8"));

        var act = () => new MergeSynchronizerPart(engine).PlanOutputs(loadout);

        act.Should().Throw<SynchronizerPreflightException>().WithMessage("*A's x.txt is not UTF-8*");
    }

    [Fact]
    public async Task AddingToAListingDoesNotChangeTheNextOne()
    {
        var loadout = await CreateLoadout();
        var part = new MergeSynchronizerPart(Engine());
        var first = part.IntrinsicFiles(loadout, Substitute.For<ILogger>());
        first[new GamePath(LocationId.Game, "Mods/loader/own.dll")] = Substitute.For<IIntrinsicFile>();

        part.IntrinsicFiles(loadout, Substitute.For<ILogger>()).Keys.Should().BeEquivalentTo([OutA, OutB]);
    }

    [Fact]
    public async Task ACheckAtARevisionThatWasNeverListedIsRefusedAsAChangedLoadout()
    {
        var loadout = await CreateLoadout();
        var part = new MergeSynchronizerPart(Engine());
        part.IntrinsicFiles(loadout, Substitute.For<ILogger>());
        using (var tx = Connection.BeginTransaction())
        {
            AddEmptyGroup(tx, loadout.LoadoutId, "later");
            await tx.Commit();
        }
        var other = Refresh(loadout);

        var act = () => part.CheckOutputs(Tree(LoadoutSourceItemType.Intrinsic, OutA, OutB), other);

        act.Should().Throw<SynchronizerPreflightException>().WithMessage("*changed while it was being applied*");
    }

    [Fact]
    public async Task AListingThatSucceedsAgainClearsTheEarlierFailure()
    {
        var loadout = await CreateLoadout();
        var engine = Engine();
        var part = new MergeSynchronizerPart(engine);
        engine.PlannedOutputs(Arg.Any<Loadout.ReadOnly>()).Returns(_ => throw new InvalidOperationException("rows unreadable"));
        part.IntrinsicFiles(loadout, Substitute.For<ILogger>());
        engine.PlannedOutputs(Arg.Any<Loadout.ReadOnly>()).Returns(Planned);
        part.IntrinsicFiles(loadout, Substitute.For<ILogger>());

        var act = () => part.CheckOutputs(Tree(LoadoutSourceItemType.Intrinsic, OutA, OutB), loadout);

        act.Should().NotThrow();
    }
}
