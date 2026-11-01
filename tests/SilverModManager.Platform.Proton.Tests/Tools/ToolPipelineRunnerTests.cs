using FluentAssertions;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Games.Tools;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Platform.Proton.Tools;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.Platform.Proton.Tests.Tools;

/// <summary>
/// A pipeline is an ordered list of catalogue tools run against one loadout; it stops at the first one that fails
/// and refuses before starting when a step is not installed.
/// </summary>
public class ToolPipelineRunnerTests(ITestOutputHelper helper) : AToolFlowTest<ToolPipelineRunnerTests>(helper)
{
    private static readonly ToolPipeline Lod = new("LOD generation", [new ToolId("texgen"), new ToolId("dyndolod")]);

    private readonly IToolManager _manager = Substitute.For<IToolManager>();
    private readonly List<string> _ran = [];
    private readonly IJobMonitor _monitor = Substitute.For<IJobMonitor>();

    private ModdingTool Tool(ToolDefinition definition) =>
        ToolFactory().Create(definition, new InstalledTool(FileSystem.FromUnsanitizedFullPath($"/tools/{definition.Id}.exe")), Definition.GameId);

    private void Installed(params ModdingTool[] tools)
    {
        _manager.GetTools(Arg.Any<Loadout.ReadOnly>()).Returns(tools);
        _manager.RunToolWithOutcome(Arg.Any<ITool>(), Arg.Any<Loadout.ReadOnly>(), Arg.Any<IJobMonitor>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var tool = call.ArgAt<ITool>(0);
                _ran.Add(tool.Name);
                return Task.FromResult(new ToolRunOutcome(call.ArgAt<Loadout.ReadOnly>(1), null, 0, 0));
            });
    }

    [Fact]
    public async Task RunsEachStepInOrderAndReportsEachFinishedLine()
    {
        var loadout = await CreateLoadout();
        Installed(Tool(DynDolod), Tool(TexGen));
        var lines = new List<string>();

        await new ToolPipelineRunner(_manager, [Lod], [TexGen, DynDolod]).Run(Lod, loadout, _monitor, lines.Add, CancellationToken.None);

        _ran.Should().Equal("TexGen", "DynDOLOD");
        lines.Should().Equal("TexGen finished (exit code 0).", "DynDOLOD finished (exit code 0).");
    }

    [Fact]
    public async Task AFailingStepStopsThePipelineAndTheLaterStepsNeverRun()
    {
        var loadout = await CreateLoadout();
        Installed(Tool(TexGen), Tool(DynDolod));
        _manager.RunToolWithOutcome(Arg.Is<ITool>(t => t.Name == "TexGen"), Arg.Any<Loadout.ReadOnly>(), Arg.Any<IJobMonitor>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ToolRunOutcome>(new InvalidOperationException("TexGen exited with code 3; see the process log.")));

        var act = () => new ToolPipelineRunner(_manager, [Lod], [TexGen, DynDolod]).Run(Lod, loadout, _monitor, _ => { }, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("TexGen exited with code 3");
        await _manager.DidNotReceive().RunToolWithOutcome(Arg.Is<ITool>(t => t.Name == "DynDOLOD"), Arg.Any<Loadout.ReadOnly>(), Arg.Any<IJobMonitor>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AStepThatIsNotInstalledRefusesBeforeAnythingRuns()
    {
        var loadout = await CreateLoadout();
        Installed(Tool(TexGen));

        var act = () => new ToolPipelineRunner(_manager, [Lod], [TexGen, DynDolod]).Run(Lod, loadout, _monitor, _ => { }, CancellationToken.None);

        (await act.Should().ThrowAsync<ToolRefusedException>()).Which.Message.Should().Contain("dyndolod").And.Contain("LOD generation");
        _ran.Should().BeEmpty();
    }

    [Fact]
    public async Task EachStepRunsAgainstTheLoadoutTheStepBeforeReturned()
    {
        var first = await CreateLoadout();
        var second = await CreateLoadout();
        Installed(Tool(TexGen), Tool(DynDolod));
        _manager.RunToolWithOutcome(Arg.Is<ITool>(t => t.Name == "TexGen"), Arg.Any<Loadout.ReadOnly>(), Arg.Any<IJobMonitor>(), Arg.Any<CancellationToken>())
            .Returns(new ToolRunOutcome(second, null, 0, 0));

        var result = await new ToolPipelineRunner(_manager, [Lod], [TexGen, DynDolod]).Run(Lod, first, _monitor, _ => { }, CancellationToken.None);

        await _manager.Received(1).RunToolWithOutcome(Arg.Is<ITool>(t => t.Name == "DynDOLOD"), second, Arg.Any<IJobMonitor>(), Arg.Any<CancellationToken>());
        result.LoadoutId.Should().Be(second.LoadoutId);
    }

    [Fact]
    public async Task FindsAPipelineByNameIgnoringCase()
    {
        var game = await CreateLoadout();
        var runner = new ToolPipelineRunner(_manager, [Lod], [TexGen, DynDolod]);

        runner.Find("lod GENERATION", game).Should().Be(Lod);
        runner.Find("nothing", game).Should().BeNull();
        runner.PipelinesFor(game).Should().Equal(Lod);
    }

    [Fact]
    public async Task APipelineIsOfferedOnlyForAGameEveryStepAppliesTo()
    {
        var game = await CreateLoadout();
        var elsewhere = Catalogue("dyndolod", "DynDOLOD", "DynDOLOD.exe") with { AppliesTo = _ => false };
        var runner = new ToolPipelineRunner(_manager, [Lod], [TexGen, elsewhere]);

        runner.PipelinesFor(game).Should().BeEmpty();
        runner.Find("LOD generation", game).Should().BeNull();
    }

    [Fact]
    public async Task APipelineWhoseStepHasNoDefinitionIsNotOffered()
    {
        var game = await CreateLoadout();

        new ToolPipelineRunner(_manager, [Lod], [TexGen]).PipelinesFor(game).Should().BeEmpty();
    }
}
