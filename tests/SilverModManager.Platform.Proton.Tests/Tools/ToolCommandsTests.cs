using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Games.Tools;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Platform.Proton.Tools;
using SilverModManager.Platform.Proton.UserTools;
using SilverModManager.Platform.Proton.Verbs;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;
using Xunit;

namespace SilverModManager.Platform.Proton.Tests.Tools;

/// <summary>
/// <c>add-tool</c>, <c>run-tool</c> and <c>run-pipeline</c>: each prints one plain line per outcome and exits
/// non-zero for a refusal or a failed run, and never prints a stack trace.
/// </summary>
public class ToolCommandsTests(ITestOutputHelper helper) : AToolFlowTest<ToolCommandsTests>(helper)
{
    private static readonly ToolPipeline Lod = new("LOD generation", [new ToolId("texgen"), new ToolId("dyndolod")]);

    private sealed class Lines : IRenderer
    {
        public List<string> Text { get; } = [];
        public string All => string.Join('\n', Text);

        public ValueTask RenderAsync(IRenderable renderable)
        {
            if (renderable is Sdk.ProxyConsole.Text text) Text.Add(text.Template.TrimEnd((char)10));
            return ValueTask.CompletedTask;
        }

        public ValueTask ClearAsync() => ValueTask.CompletedTask;
    }

    private readonly IToolManager _manager = Substitute.For<IToolManager>();
    private readonly Lines _out = new();

    private ToolCommands Commands(params ToolDefinition[] definitions)
    {
        var runner = new ToolPipelineRunner(_manager, [Lod], [TexGen, DynDolod]);
        return new ToolCommands(new ToolInstaller(Connection, new KnownToolDetector(definitions)), _manager, runner, Substitute.For<IJobMonitor>(), NullLogger<ToolCommands>.Instance);
    }

    private ModdingTool Tool(ToolDefinition definition) =>
        ToolFactory().Create(definition, new InstalledTool(FileSystem.FromUnsanitizedFullPath($"/tools/{definition.Id}.exe")), Definition.GameId);

    private void Installed(params ITool[] tools) => _manager.GetTools(Arg.Any<Loadout.ReadOnly>()).Returns(tools);

    private void Outcome(string? group = null, int? exit = 0, int captured = 0) =>
        _manager.RunToolWithOutcome(Arg.Any<ITool>(), Arg.Any<Loadout.ReadOnly>(), Arg.Any<IJobMonitor>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new ToolRunOutcome(call.ArgAt<Loadout.ReadOnly>(1), group, exit, captured)));

    [Fact]
    public async Task AddToolNamesEachToolItSavedAndExitsZero()
    {
        var loadout = await CreateLoadout();
        var folder = TemporaryFileManager.CreateFolder("tools").Path;
        File.WriteAllText((folder / "TexGenx64.exe").GetFullPath(), "exe");

        var exit = await Commands(TexGen).AddAsync(_out, loadout, folder.GetFullPath());

        exit.Should().Be(0);
        _out.All.Should().Contain("Added TexGen");
    }

    [Fact]
    public async Task AddToolSaysWhenEverythingWasAlreadyThere()
    {
        var loadout = await CreateLoadout();
        var folder = TemporaryFileManager.CreateFolder("tools").Path;
        File.WriteAllText((folder / "TexGenx64.exe").GetFullPath(), "exe");
        await Commands(TexGen).AddAsync(_out, loadout, folder.GetFullPath());

        var exit = await Commands(TexGen).AddAsync(_out, loadout, folder.GetFullPath());

        exit.Should().Be(0);
        _out.Text.Last().Should().Contain("TexGen is already added");
    }

    [Fact]
    public async Task AddToolWithARelativePathIsRefused()
    {
        var loadout = await CreateLoadout();

        var exit = await Commands(TexGen).AddAsync(_out, loadout, "tools/TexGen.exe");

        exit.Should().Be(1);
        _out.All.Should().Contain("absolute path");
    }

    [Fact]
    public async Task AddToolWithNothingKnownPrintsTheRefusal()
    {
        var loadout = await CreateLoadout();
        var folder = TemporaryFileManager.CreateFolder("tools").Path;

        var exit = await Commands(TexGen).AddAsync(_out, loadout, folder.GetFullPath());

        exit.Should().Be(1);
        _out.All.Should().Contain("no known tool");
    }

    [Fact]
    public async Task RunToolFindsTheToolByNameOrIdAndPrintsTheFinishedLine()
    {
        var loadout = await CreateLoadout();
        Installed(Tool(TexGen), Tool(DynDolod));
        Outcome(group: "DynDOLOD Output", exit: 0, captured: 1204);

        var exit = await Commands().RunToolAsync(_out, loadout, "dyndolod", cleanPlugin: null, CancellationToken.None);

        exit.Should().Be(0);
        _out.All.Should().Be("DynDOLOD finished (exit code 0). Captured 1,204 files into 'DynDOLOD Output'.");
        await _manager.Received(1).RunToolWithOutcome(Arg.Is<ITool>(t => t.Name == "DynDOLOD"), loadout, Arg.Any<IJobMonitor>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunToolIgnoresCaseInTheName()
    {
        var loadout = await CreateLoadout();
        Installed(Tool(TexGen));
        Outcome();

        (await Commands().RunToolAsync(_out, loadout, "texGEN", null, CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task RunToolWithAnUnknownNameListsWhatIsAvailable()
    {
        var loadout = await CreateLoadout();
        Installed(Tool(TexGen), Tool(DynDolod));

        var exit = await Commands().RunToolAsync(_out, loadout, "nothing", null, CancellationToken.None);

        exit.Should().Be(1);
        _out.All.Should().Contain("No tool called nothing").And.Contain("TexGen").And.Contain("DynDOLOD");
        await _manager.DidNotReceiveWithAnyArgs().RunToolWithOutcome(default!, default, default!, default);
    }

    [Fact]
    public async Task RunToolWithCleanPluginRunsTheCleaningMode()
    {
        var loadout = await CreateLoadout();
        var xedit = Catalogue("xedit", "xEdit", "xEdit.exe") with
        {
            QuickAutoClean = new ToolVariant("Quick Auto Clean", ArgumentTemplate.Of("{Plugin}"), ExitCodePolicy.ZeroIsSuccess),
        };
        Installed(Tool(xedit));
        Outcome();

        var exit = await Commands().RunToolAsync(_out, loadout, "xedit", "Update.esm", CancellationToken.None);

        exit.Should().Be(0);
        await _manager.Received(1).RunToolWithOutcome(Arg.Is<ITool>(t => t.Name == "xEdit: Quick Auto Clean"), loadout, Arg.Any<IJobMonitor>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunToolWithCleanPluginOnAToolWithoutThatModeIsRefused()
    {
        var loadout = await CreateLoadout();
        Installed(Tool(TexGen));

        var exit = await Commands().RunToolAsync(_out, loadout, "texgen", "Update.esm", CancellationToken.None);

        exit.Should().Be(1);
        _out.All.Should().Contain("TexGen cannot clean a plugin");
    }

    [Fact]
    public async Task ARunThatFailsPrintsItsMessageAndExitsOne()
    {
        var loadout = await CreateLoadout();
        Installed(Tool(TexGen));
        _manager.RunToolWithOutcome(default!, default, default!, default).ReturnsForAnyArgs(
            Task.FromException<ToolRunOutcome>(new InvalidOperationException("TexGen exited with code 3; see the process log.")));

        var exit = await Commands().RunToolAsync(_out, loadout, "texgen", null, CancellationToken.None);

        exit.Should().Be(1);
        _out.All.Should().Be("TexGen exited with code 3; see the process log.");
    }

    [Fact]
    public async Task ACrashInsideARunPointsAtTheLogWithoutItsDetail()
    {
        var loadout = await CreateLoadout();
        Installed(Tool(TexGen));
        _manager.RunToolWithOutcome(default!, default, default!, default).ReturnsForAnyArgs(
            Task.FromException<ToolRunOutcome>(new NullReferenceException("secret internal detail")));

        var exit = await Commands().RunToolAsync(_out, loadout, "texgen", null, CancellationToken.None);

        exit.Should().Be(1);
        _out.All.Should().Contain("log").And.NotContain("secret internal detail");
    }

    [Fact]
    public async Task RunPipelinePrintsEachStepsLine()
    {
        var loadout = await CreateLoadout();
        Installed(Tool(TexGen), Tool(DynDolod));
        Outcome();

        var exit = await Commands().RunPipelineAsync(_out, loadout, "LOD generation", CancellationToken.None);

        exit.Should().Be(0);
        _out.Text.Should().Equal("TexGen finished (exit code 0).", "DynDOLOD finished (exit code 0).");
    }

    [Fact]
    public async Task RunPipelineWithAnUnknownNameListsThePipelines()
    {
        var loadout = await CreateLoadout();

        var exit = await Commands().RunPipelineAsync(_out, loadout, "nothing", CancellationToken.None);

        exit.Should().Be(1);
        _out.All.Should().Contain("No pipeline called nothing").And.Contain("LOD generation");
    }

    [Fact]
    public async Task RunPipelineThatCannotStartPrintsTheRefusal()
    {
        var loadout = await CreateLoadout();
        Installed(Tool(TexGen));

        var exit = await Commands().RunPipelineAsync(_out, loadout, "LOD generation", CancellationToken.None);

        exit.Should().Be(1);
        _out.All.Should().Contain("dyndolod");
    }

    [Fact]
    public async Task RunPipelineForAGameItDoesNotApplyToIsRefusedAsUnknown()
    {
        var loadout = await CreateLoadout();
        var runner = new ToolPipelineRunner(_manager, [Lod], [TexGen with { AppliesTo = _ => false }, DynDolod]);
        var commands = new ToolCommands(new ToolInstaller(Connection, new KnownToolDetector([])), _manager, runner, Substitute.For<IJobMonitor>(), NullLogger<ToolCommands>.Instance);

        var exit = await commands.RunPipelineAsync(_out, loadout, "LOD generation", CancellationToken.None);

        exit.Should().Be(1);
        _out.All.Should().Contain("No pipeline called LOD generation");
        await _manager.DidNotReceive().RunToolWithOutcome(Arg.Any<ITool>(), Arg.Any<Loadout.ReadOnly>(), Arg.Any<IJobMonitor>(), Arg.Any<CancellationToken>());
    }
}
