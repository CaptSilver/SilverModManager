using System.Diagnostics.CodeAnalysis;
using Avalonia.Platform.Storage;
using Avalonia.Input.Platform;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Loadouts;
using NSubstitute;
using SilverModManager.App.UI;
using SilverModManager.DataModel;
using SilverModManager.Abstractions.Games.Tools;
using SilverModManager.Platform.Proton.Tools;
using SilverModManager.Platform.Proton.UserTools;
using SilverModManager.App.UI.Pages.Diagnostics.PrefixSetup;
using SilverModManager.App.UI.Pages.Tools;
using SilverModManager.App.UI.Windows;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;
using R3;
using Unit = R3.Unit;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// The Tools page lists the loadout's tools and runs a picked Windows program through the tool manager, so the
/// loadout is synchronized around it like around any other tool.
/// </summary>
public class ToolsPageViewModelTests(IServiceProvider provider) : AUiTest(provider)
{
    // Written straight to the database: creating a loadout through the manager also synchronizes it, which
    // needs a game file hash database these tests have no reason to carry.
    private async Task<Loadout.ReadOnly> CreateLoadout()
    {
        var installation = Provider.GetRequiredService<IGameRegistry>().LocateGameInstallations().First();

        using var tx = Connection.BeginTransaction();
        var metadata = new GameInstallMetadata.New(tx)
        {
            Store = installation.LocatorResult.Store,
            Path = installation.Locations[LocationId.Game].Path.ToString(),
            Game = installation.Game.GameId,
            GameId = installation.Game.NexusModsGameId.Value,
            Name = installation.Game.DisplayName,
        };
        var loadout = new Loadout.New(tx)
        {
            Name = Guid.NewGuid().ToString(),
            ShortName = "A",
            InstallationId = metadata,
            Revision = 0,
            LoadoutKind = LoadoutKind.Default,
            LocatorIds = installation.LocatorResult.LocatorIds.Distinct().ToList(),
            GameVersion = VanityVersion.From("1.0"),
        };
        var result = await tx.Commit();
        return result.Remap(loadout);
    }

    private IPrefixInspector _inspector = Substitute.For<IPrefixInspector>();
    private readonly IPrefixSetupLauncher _prefixSetup = Substitute.For<IPrefixSetupLauncher>();

    private ModdingToolFactory CreateToolFactory(FakeToolRunner toolRunner)
        => new(toolRunner, Substitute.For<IProcessRunner>(), new ToolArgumentResolver(), new FakePrefixResolver(), new ToolOutputCapture(Connection, Substitute.For<IFileStore>()), new ToolPrefixSetup(toolRunner, new FakePrefixResolver(), _inspector));

    private static ToolDefinition Catalogue(string id, string display, params string[] exeNames) => new()
    {
        Id = new ToolId(id),
        DisplayName = display,
        AppliesTo = _ => true,
        ExecutableNames = _ => exeNames,
        Arguments = ArgumentTemplate.Empty,
    };

    private ToolsPageViewModel CreateViewModel(FakeToolManager toolManager, FakeToolRunner toolRunner, FakeFilePicker picker, ToolPipeline[]? pipelines = null, ToolDefinition[]? definitions = null, ToolDefinition[]? pipelineDefinitions = null)
    {
        return new ToolsPageViewModel(
            Provider.GetRequiredService<IWindowManager>(),
            NullLogger<ToolsPageViewModel>.Instance,
            Connection,
            toolManager,
            toolRunner,
            CreateToolFactory(toolRunner),
            new FakePrefixResolver(),
            picker,
            Provider.GetRequiredService<IJobMonitor>(),
            new ToolInstaller(Connection, new KnownToolDetector(definitions ?? [])),
            new ToolPipelineRunner(toolManager, pipelines ?? [], pipelineDefinitions ?? []),
            _prefixSetup
        );
    }

    [Fact]
    public async Task Lists_TheToolsTheToolManagerReportsForTheLoadout()
    {
        var loadout = await CreateLoadout();
        var tools = new ITool[] { new StubTool("First", loadout.Game.GameId), new StubTool("Second", loadout.Game.GameId) };
        var vm = CreateViewModel(new FakeToolManager() { Tools = tools }, new FakeToolRunner(), new FakeFilePicker());

        vm.LoadoutId = loadout.LoadoutId;

        vm.Tools.Select(t => t.Name).Should().Equal("First", "Second");
    }

    [Fact]
    public async Task RunToolCommand_RunsTheToolThroughTheToolManager()
    {
        var loadout = await CreateLoadout();
        var tool = new StubTool("First", loadout.Game.GameId);
        var toolManager = new FakeToolManager() { Tools = [tool] };
        var vm = CreateViewModel(toolManager, new FakeToolRunner(), new FakeFilePicker());
        vm.LoadoutId = loadout.LoadoutId;

        await vm.RunToolAsync(tool, TestContext.Current.CancellationToken);

        toolManager.Ran.Should().Equal(tool);
    }

    [Fact]
    public async Task RunWindowsProgram_RunsAWindowsProgramToolThatReachesTheToolRunnerWithTheInstallationAndExe()
    {
        var loadout = await CreateLoadout();
        var exe = FileSystem.Shared.GetKnownPath(KnownPath.TempDirectory).Combine($"{Guid.NewGuid()}/SSEEdit.exe");
        var toolManager = new FakeToolManager();
        var toolRunner = new FakeToolRunner();
        var vm = CreateViewModel(toolManager, toolRunner, new FakeFilePicker { Result = [exe] });
        vm.LoadoutId = loadout.LoadoutId;

        await vm.RunWindowsProgramAsync(TestContext.Current.CancellationToken);

        toolManager.Ran.Should().ContainSingle().Which.Should().BeOfType<ModdingTool>().Which.Exe.Should().Be(exe);
        var call = toolRunner.Calls.Should().ContainSingle().Subject;
        call.Installation.Should().Be(loadout.InstallationInstance);
        call.Exe.Should().Be(exe);
    }

    [Fact]
    public async Task RunWindowsProgram_DoesNothingWhenThePickerIsCancelled()
    {
        var loadout = await CreateLoadout();
        var toolManager = new FakeToolManager();
        var vm = CreateViewModel(toolManager, new FakeToolRunner(), new FakeFilePicker { Result = [] });
        vm.LoadoutId = loadout.LoadoutId;

        await vm.RunWindowsProgramAsync(TestContext.Current.CancellationToken);

        toolManager.Ran.Should().BeEmpty();
    }

    [Fact]
    public async Task ShowsThePrefixAndStrategyWhenAProgramCanRun()
    {
        var loadout = await CreateLoadout();
        var vm = CreateViewModel(new FakeToolManager(), new FakeToolRunner { Strategy = "umu" }, new FakeFilePicker());

        vm.LoadoutId = loadout.LoadoutId;

        vm.CanRunWindowsProgram.Should().BeTrue();
        ((System.Windows.Input.ICommand)vm.RunWindowsProgramCommand).CanExecute(null).Should().BeTrue();
        vm.Strategy.Should().Be("umu");
        vm.PrefixPath.Should().Be(FakePrefixResolver.PrefixPath.ToString());
        vm.Diagnostic.Should().BeNull();
    }

    [Fact]
    public async Task DisablesRunningAProgramAndShowsTheDiagnosticWhenNothingCanRun()
    {
        var loadout = await CreateLoadout();
        var toolManager = new FakeToolManager();
        var toolRunner = new FakeToolRunner { Diagnostic = "No Wine prefix was found." };
        var vm = CreateViewModel(toolManager, toolRunner, new FakeFilePicker { Result = [FileSystem.Shared.GetKnownPath(KnownPath.TempDirectory).Combine("x.exe")] });

        vm.LoadoutId = loadout.LoadoutId;

        vm.CanRunWindowsProgram.Should().BeFalse();
        ((System.Windows.Input.ICommand)vm.RunWindowsProgramCommand).CanExecute(null).Should().BeFalse();
        vm.Diagnostic.Should().Be("No Wine prefix was found.");
        vm.Strategy.Should().BeNull();
    }

    [Fact]
    public async Task RunWindowsProgram_RefusesWhenNothingCanRunInsteadOfDoingNothing()
    {
        var loadout = await CreateLoadout();
        var toolManager = new FakeToolManager();
        var vm = CreateViewModel(toolManager, new FakeToolRunner { Diagnostic = "No Wine prefix was found." }, new FakeFilePicker { Result = [FileSystem.Shared.GetKnownPath(KnownPath.TempDirectory).Combine("x.exe")] });
        vm.LoadoutId = loadout.LoadoutId;

        var act = () => vm.RunWindowsProgramAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<SilverModManager.Sdk.Errors.UserFacingException>().WithMessage("*No Wine prefix was found.*");
        toolManager.Ran.Should().BeEmpty();
    }

    private static string TempExe(string name = "SSEEdit.exe")
        => FileSystem.Shared.GetKnownPath(KnownPath.TempDirectory).Combine($"{Guid.NewGuid()}/{name}").ToString();

    [Fact]
    public async Task AddUserTool_PersistsTheToolAndListsIt()
    {
        var loadout = await CreateLoadout();
        var vm = CreateViewModel(new FakeToolManager(), new FakeToolRunner(), new FakeFilePicker());
        vm.LoadoutId = loadout.LoadoutId;
        var exe = TempExe();

        await vm.AddUserTool(new UserToolDraft("xEdit", exe, "-SSE", ToolRunVia.Wine));

        var tool = vm.UserTools.Should().ContainSingle().Subject;
        tool.Name.Should().Be("xEdit");
        tool.Exe.Should().Be(exe);
        tool.Arguments.Should().Be("-SSE");
        tool.RunVia.Should().Be(ToolRunVia.Wine);
        tool.InstallationId.Should().Be(loadout.InstallationId);
    }

    [Fact]
    public async Task UserTools_SurviveRebuildingTheViewModel()
    {
        var loadout = await CreateLoadout();
        var first = CreateViewModel(new FakeToolManager(), new FakeToolRunner(), new FakeFilePicker());
        first.LoadoutId = loadout.LoadoutId;
        await first.AddUserTool(new UserToolDraft("xEdit", TempExe(), "", ToolRunVia.Auto));

        var second = CreateViewModel(new FakeToolManager(), new FakeToolRunner(), new FakeFilePicker());
        second.LoadoutId = loadout.LoadoutId;

        second.UserTools.Select(t => t.Name).Should().Equal("xEdit");
    }

    [Fact]
    public async Task UpdateUserTool_ReplacesItsFields()
    {
        var loadout = await CreateLoadout();
        var vm = CreateViewModel(new FakeToolManager(), new FakeToolRunner(), new FakeFilePicker());
        vm.LoadoutId = loadout.LoadoutId;
        await vm.AddUserTool(new UserToolDraft("xEdit", TempExe(), "", ToolRunVia.Auto));
        var newExe = TempExe("FO4Edit.exe");

        await vm.UpdateUserTool(vm.UserTools.Single(), new UserToolDraft("FO4Edit", newExe, "-FO4", ToolRunVia.Umu));

        var tool = vm.UserTools.Should().ContainSingle().Subject;
        tool.Name.Should().Be("FO4Edit");
        tool.Exe.Should().Be(newExe);
        tool.Arguments.Should().Be("-FO4");
        tool.RunVia.Should().Be(ToolRunVia.Umu);
    }

    [Fact]
    public async Task UpdateUserTool_RefusesARemovedToolInsteadOfWritingItBack()
    {
        var loadout = await CreateLoadout();
        var vm = CreateViewModel(new FakeToolManager(), new FakeToolRunner(), new FakeFilePicker());
        vm.LoadoutId = loadout.LoadoutId;
        await vm.AddUserTool(new UserToolDraft("xEdit", TempExe(), "", ToolRunVia.Auto));
        var removed = vm.UserTools.Single();
        await vm.RemoveUserTool(removed);

        var act = () => vm.UpdateUserTool(removed, new UserToolDraft("FO4Edit", TempExe(), "", ToolRunVia.Auto));

        await act.Should().ThrowAsync<ArgumentException>();
        UserTool.Load(Connection.Db, removed.Id).IsValid().Should().BeFalse();
    }

    [Fact]
    public async Task RemoveUserTool_DropsItFromTheListAndFromTheDatabase()
    {
        var loadout = await CreateLoadout();
        var vm = CreateViewModel(new FakeToolManager(), new FakeToolRunner(), new FakeFilePicker());
        vm.LoadoutId = loadout.LoadoutId;
        await vm.AddUserTool(new UserToolDraft("keep", TempExe(), "", ToolRunVia.Auto));
        await vm.AddUserTool(new UserToolDraft("drop", TempExe(), "", ToolRunVia.Auto));

        await vm.RemoveUserTool(vm.UserTools.Single(t => t.Name == "drop"));

        vm.UserTools.Select(t => t.Name).Should().Equal("keep");
        UserTool.FindByInstallation(Connection.Db, loadout.InstallationId).Select(t => t.Name).Should().Equal("keep");
    }

    [Theory]
    [InlineData("", "/abs/prog.exe")]
    [InlineData("  ", "/abs/prog.exe")]
    [InlineData("name", "")]
    [InlineData("name", "relative/prog.exe")]
    public async Task AddUserTool_RefusesAnEmptyNameOrANonAbsoluteProgramAndStoresNothing(string name, string exe)
    {
        var loadout = await CreateLoadout();
        var vm = CreateViewModel(new FakeToolManager(), new FakeToolRunner(), new FakeFilePicker());
        vm.LoadoutId = loadout.LoadoutId;

        var act = () => vm.AddUserTool(new UserToolDraft(name, exe, "", ToolRunVia.Auto));

        await act.Should().ThrowAsync<ArgumentException>();
        vm.UserTools.Should().BeEmpty();
        UserTool.FindByInstallation(Connection.Db, loadout.InstallationId).Should().BeEmpty();
    }

    [Fact]
    public async Task TheToolManager_ListsAUserToolForItsInstallationOnly()
    {
        var withTool = await CreateLoadout();
        var other = await CreateLoadout();
        var vm = CreateViewModel(new FakeToolManager(), new FakeToolRunner(), new FakeFilePicker());
        vm.LoadoutId = withTool.LoadoutId;
        await vm.AddUserTool(new UserToolDraft("xEdit", TempExe(), "", ToolRunVia.Native));
        var toolManager = Provider.GetRequiredService<IToolManager>();

        toolManager.GetTools(Loadout.Load(Connection.Db, withTool.LoadoutId)).Select(t => t.Name).Should().Contain("xEdit");
        toolManager.GetTools(Loadout.Load(Connection.Db, other.LoadoutId)).Select(t => t.Name).Should().NotContain("xEdit");
    }

    private ModdingTool CatalogueTool(FakeToolRunner runner, ToolDefinition definition, string exe = "/tools/tool.exe")
        => CreateToolFactory(runner).Create(definition, new InstalledTool(FileSystem.Shared.FromUnsanitizedFullPath(exe)), GameId.From("any"));

    [Fact]
    public async Task CatalogueTools_ListsOnlyTheToolsAddedFromTheCatalogue()
    {
        var loadout = await CreateLoadout();
        var runner = new FakeToolRunner();
        var tools = new ITool[] { new StubTool("Launch", loadout.Game.GameId), CatalogueTool(runner, Catalogue("texgen", "TexGen", "TexGen.exe")), CreateToolFactory(runner).Create(ToolDefinition.AdHoc(loadout.Game.GameId, "Mine", "m.exe"), new InstalledTool(FileSystem.Shared.FromUnsanitizedFullPath("/tools/m.exe")), loadout.Game.GameId) };
        var vm = CreateViewModel(new FakeToolManager { Tools = tools }, runner, new FakeFilePicker());

        vm.LoadoutId = loadout.LoadoutId;

        vm.CatalogueTools.Select(t => t.Name).Should().Equal("TexGen");
    }

    [Fact]
    public async Task CatalogueTools_NameThePrefixComponentsTheToolIsMissing()
    {
        var loadout = await CreateLoadout();
        var vc = SilverModManager.Sdk.Games.PrefixComponents.VisualCppRuntime;
        _inspector.Inspect(Arg.Any<ResolvedPrefix>(), Arg.Any<IReadOnlyList<PrefixComponentId>>())
            .Returns(call => call.ArgAt<IReadOnlyList<PrefixComponentId>>(1).Select(id => new PrefixComponentStatus(id, PrefixComponentState.Missing, "test")).ToArray());
        var runner = new FakeToolRunner();
        var needy = Catalogue("xedit", "xEdit", "xEdit.exe") with { Prefix = new ToolPrefixRequirements { Components = [vc.Id] } };
        var vm = CreateViewModel(new FakeToolManager { Tools = [CatalogueTool(runner, needy)] }, runner, new FakeFilePicker());

        vm.LoadoutId = loadout.LoadoutId;

        var row = vm.CatalogueTools.Should().ContainSingle().Subject;
        row.MissingPrerequisites.Should().Equal(vc.DisplayName);
        row.PrerequisiteMessage.Should().Be($"Needs {vc.DisplayName} in the game's Wine prefix. Install it before running.");
    }

    [Fact]
    public async Task CatalogueTools_WithNothingMissingHaveNoPrerequisiteMessage()
    {
        var loadout = await CreateLoadout();
        var runner = new FakeToolRunner();
        var vm = CreateViewModel(new FakeToolManager { Tools = [CatalogueTool(runner, Catalogue("texgen", "TexGen", "TexGen.exe"))] }, runner, new FakeFilePicker());

        vm.LoadoutId = loadout.LoadoutId;

        vm.CatalogueTools.Single().PrerequisiteMessage.Should().BeNull();
    }

    [Fact]
    public async Task ThePluginPickerIsEmptyForAGameThatOffersNoPlugins()
    {
        var loadout = await CreateLoadout();
        var vm = CreateViewModel(new FakeToolManager(), new FakeToolRunner(), new FakeFilePicker());

        vm.LoadoutId = loadout.LoadoutId;

        vm.Plugins.Should().BeEmpty();
    }

    [Fact]
    public async Task ARunStatesTheExitCodeAndWhatWasCapturedOnThePage()
    {
        var loadout = await CreateLoadout();
        var tool = new StubTool("DynDOLOD", loadout.Game.GameId);
        var manager = new FakeToolManager { Tools = [tool], Report = _ => ("DynDOLOD Output", 0, 1204) };
        var vm = CreateViewModel(manager, new FakeToolRunner(), new FakeFilePicker());
        vm.LoadoutId = loadout.LoadoutId;

        await vm.RunToolAsync(tool, TestContext.Current.CancellationToken);

        vm.LastRunMessage.Should().Be("DynDOLOD finished (exit code 0). Captured 1,204 files into 'DynDOLOD Output'.");
    }

    [Fact]
    public async Task AFailedRunShowsItsMessageOnThePageInsteadOfThrowing()
    {
        var loadout = await CreateLoadout();
        var runner = new FakeToolRunner();
        var vm = CreateViewModel(new FakeToolManager(), runner, new FakeFilePicker());
        vm.LoadoutId = loadout.LoadoutId;
        var failing = new ThrowingTool("TexGen exited with code 3; see the process log.", loadout.Game.GameId);

        await vm.RunToolReportingAsync(failing, TestContext.Current.CancellationToken);

        vm.LastRunMessage.Should().Be("TexGen exited with code 3; see the process log.");
        vm.IsRunning.Should().BeFalse();
        vm.RunningMessage.Should().BeNull();
    }

    [Fact]
    public async Task WhileAToolRunsThePageSaysSoAndAfterwardsItDoesNot()
    {
        var loadout = await CreateLoadout();
        var tool = new StubTool("DynDOLOD", loadout.Game.GameId);
        var gate = new TaskCompletionSource();
        var manager = new FakeToolManager { Tools = [tool], Gate = gate.Task };
        var vm = CreateViewModel(manager, new FakeToolRunner(), new FakeFilePicker());
        vm.LoadoutId = loadout.LoadoutId;

        var run = vm.RunToolAsync(tool, TestContext.Current.CancellationToken);

        vm.IsRunning.Should().BeTrue();
        vm.RunningMessage.Should().Be("Running DynDOLOD for less than a minute");
        gate.SetResult();
        await run;
        vm.IsRunning.Should().BeFalse();
        vm.RunningMessage.Should().BeNull();
    }

    [Fact]
    public async Task CancelStopsTheRunByCancellingTheTokenTheToolManagerGot()
    {
        var loadout = await CreateLoadout();
        var tool = new StubTool("DynDOLOD", loadout.Game.GameId);
        var manager = new FakeToolManager { Tools = [tool], Gate = new TaskCompletionSource().Task };
        var vm = CreateViewModel(manager, new FakeToolRunner(), new FakeFilePicker());
        vm.LoadoutId = loadout.LoadoutId;
        var run = vm.RunToolReportingAsync(tool, TestContext.Current.CancellationToken).AsTask();
        // The run hops to the thread pool before it reaches the manager; a Cancel before then never starts it.
        await manager.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        vm.CancelRun();
        await run;

        manager.Tokens.Single().IsCancellationRequested.Should().BeTrue();
        vm.LastRunMessage.Should().Be("DynDOLOD was cancelled.");
        vm.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task CancelWithNothingRunningDoesNothing()
    {
        var vm = CreateViewModel(new FakeToolManager(), new FakeToolRunner(), new FakeFilePicker());
        var act = () => vm.CancelRun();

        act.Should().NotThrow();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task QuickAutoCleanRunsTheToolsCleaningModeOnThePickedPlugin()
    {
        var loadout = await CreateLoadout();
        var runner = new FakeToolRunner();
        var xedit = Catalogue("xedit", "xEdit", "xEdit.exe") with
        {
            QuickAutoClean = new ToolVariant("Quick Auto Clean", ArgumentTemplate.Of("{Plugin}"), ExitCodePolicy.ZeroIsSuccess),
        };
        var tool = CatalogueTool(runner, xedit);
        var manager = new FakeToolManager { Tools = [tool] };
        var vm = CreateViewModel(manager, runner, new FakeFilePicker());
        vm.LoadoutId = loadout.LoadoutId;

        await vm.RunQuickAutoCleanAsync(vm.CatalogueTools.Single(), "Update.esm", TestContext.Current.CancellationToken);

        manager.Ran.Single().Name.Should().Be("xEdit: Quick Auto Clean");
    }

    [Fact]
    public async Task QuickAutoCleanIsRefusedForAToolWithoutThatMode()
    {
        var loadout = await CreateLoadout();
        var runner = new FakeToolRunner();
        var vm = CreateViewModel(new FakeToolManager { Tools = [CatalogueTool(runner, Catalogue("texgen", "TexGen", "TexGen.exe"))] }, runner, new FakeFilePicker());
        vm.LoadoutId = loadout.LoadoutId;

        var act = () => vm.RunQuickAutoCleanAsync(vm.CatalogueTools.Single(), "Update.esm", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<SilverModManager.Sdk.Errors.UserFacingException>().WithMessage("*TexGen cannot clean a plugin*");
    }

    [Fact]
    public async Task APipelineRunsItsToolsAndShowsEachFinishedLine()
    {
        var loadout = await CreateLoadout();
        var runner = new FakeToolRunner();
        var lod = new ToolPipeline("LOD generation", [new ToolId("texgen"), new ToolId("dyndolod")]);
        var tools = new ITool[] { CatalogueTool(runner, Catalogue("texgen", "TexGen", "TexGen.exe")), CatalogueTool(runner, Catalogue("dyndolod", "DynDOLOD", "DynDOLOD.exe")) };
        var manager = new FakeToolManager { Tools = tools, Report = _ => (null, 0, 0) };
        var vm = CreateViewModel(manager, runner, new FakeFilePicker(), [lod], pipelineDefinitions: [Catalogue("texgen", "TexGen", "TexGen.exe"), Catalogue("dyndolod", "DynDOLOD", "DynDOLOD.exe")]);
        vm.LoadoutId = loadout.LoadoutId;

        vm.Pipelines.Should().Equal(lod);
        await vm.RunPipelineAsync(lod, TestContext.Current.CancellationToken);

        manager.Ran.Select(t => t.Name).Should().Equal("TexGen", "DynDOLOD");
        vm.LastRunMessage.Should().Be("TexGen finished (exit code 0).\nDynDOLOD finished (exit code 0).");
    }

    [Fact]
    public async Task APipelineIsNotListedForAGameItsToolsDoNotApplyTo()
    {
        var loadout = await CreateLoadout();
        var lod = new ToolPipeline("LOD generation", [new ToolId("texgen")]);
        var elsewhere = Catalogue("texgen", "TexGen", "TexGen.exe") with { AppliesTo = _ => false };
        var vm = CreateViewModel(new FakeToolManager(), new FakeToolRunner(), new FakeFilePicker(), [lod], pipelineDefinitions: [elsewhere]);

        vm.LoadoutId = loadout.LoadoutId;

        vm.Pipelines.Should().BeEmpty();
    }

    [Fact]
    public async Task ASecondRunWhileOneIsGoingIsRefusedAndCancelStillStopsTheFirst()
    {
        var loadout = await CreateLoadout();
        var first = new StubTool("DynDOLOD", loadout.Game.GameId);
        var second = new StubTool("TexGen", loadout.Game.GameId);
        var manager = new FakeToolManager { Tools = [first, second], Gate = new TaskCompletionSource().Task };
        var vm = CreateViewModel(manager, new FakeToolRunner(), new FakeFilePicker());
        vm.LoadoutId = loadout.LoadoutId;
        var run = vm.RunToolReportingAsync(first, TestContext.Current.CancellationToken).AsTask();

        await vm.RunToolReportingAsync(second, TestContext.Current.CancellationToken);

        manager.Ran.Should().Equal(first);
        vm.IsRunning.Should().BeTrue();
        vm.LastRunMessage.Should().Contain("DynDOLOD is still running");
        vm.CancelRun();
        await run;
        manager.Tokens.Single().IsCancellationRequested.Should().BeTrue();
        vm.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task APipelineOrACleanIsRefusedWhileAToolRuns()
    {
        var loadout = await CreateLoadout();
        var runner = new FakeToolRunner();
        var xedit = Catalogue("xedit", "xEdit", "xEdit.exe") with
        {
            QuickAutoClean = new ToolVariant("Quick Auto Clean", ArgumentTemplate.Of("{Plugin}"), ExitCodePolicy.ZeroIsSuccess),
        };
        var running = new StubTool("DynDOLOD", loadout.Game.GameId);
        var lod = new ToolPipeline("LOD generation", [new ToolId("xedit")]);
        var tool = CatalogueTool(runner, xedit);
        var manager = new FakeToolManager { Tools = [running, tool], Gate = new TaskCompletionSource().Task };
        var vm = CreateViewModel(manager, runner, new FakeFilePicker(), [lod], pipelineDefinitions: [xedit]);
        vm.LoadoutId = loadout.LoadoutId;
        var run = vm.RunToolReportingAsync(running, TestContext.Current.CancellationToken).AsTask();

        await vm.RunPipelineReportingAsync(lod, TestContext.Current.CancellationToken);
        await ((Func<Task>)(() => vm.RunQuickAutoCleanAsync(vm.CatalogueTools.Single(), "Update.esm", TestContext.Current.CancellationToken))).Should().ThrowAsync<SilverModManager.Sdk.Errors.UserFacingException>();

        manager.Ran.Should().Equal(running);
        vm.CancelRun();
        await run;
    }

    [Fact]
    public async Task InstallPrerequisitesSetsUpTheMissingComponentsThenChecksTheRowAgain()
    {
        var loadout = await CreateLoadout();
        var vc = SilverModManager.Sdk.Games.PrefixComponents.VisualCppRuntime;
        var state = PrefixComponentState.Missing;
        _inspector.Inspect(Arg.Any<ResolvedPrefix>(), Arg.Any<IReadOnlyList<PrefixComponentId>>())
            .Returns(call => call.ArgAt<IReadOnlyList<PrefixComponentId>>(1).Select(id => new PrefixComponentStatus(id, state, "test")).ToArray());
        _prefixSetup.ShowAsync(loadout.LoadoutId, Arg.Any<IReadOnlyList<PrefixComponentId>>()).Returns(_ =>
        {
            state = PrefixComponentState.Installed;
            return Task.CompletedTask;
        });
        var runner = new FakeToolRunner();
        var needy = Catalogue("xedit", "xEdit", "xEdit.exe") with { Prefix = new ToolPrefixRequirements { Components = [vc.Id] } };
        var vm = CreateViewModel(new FakeToolManager { Tools = [CatalogueTool(runner, needy)] }, runner, new FakeFilePicker());
        vm.LoadoutId = loadout.LoadoutId;

        await vm.InstallPrerequisitesAsync(vm.CatalogueTools.Single());

        await _prefixSetup.Received(1).ShowAsync(loadout.LoadoutId, Arg.Is<IReadOnlyList<PrefixComponentId>>(ids => ids.SequenceEqual(new[] { vc.Id })));
        vm.CatalogueTools.Single().PrerequisiteMessage.Should().BeNull();
    }

    [Fact]
    public async Task APipelineWithAMissingToolShowsTheRefusalAndRunsNothing()
    {
        var loadout = await CreateLoadout();
        var lod = new ToolPipeline("LOD generation", [new ToolId("texgen")]);
        var manager = new FakeToolManager();
        var vm = CreateViewModel(manager, new FakeToolRunner(), new FakeFilePicker(), [lod], pipelineDefinitions: [Catalogue("texgen", "TexGen", "TexGen.exe")]);
        vm.LoadoutId = loadout.LoadoutId;

        await vm.RunPipelineReportingAsync(lod, TestContext.Current.CancellationToken);

        vm.LastRunMessage.Should().Contain("texgen").And.Contain("LOD generation");
        manager.Ran.Should().BeEmpty();
    }

    [Fact]
    public async Task AddingAToolFolderSavesTheCatalogueToolsInItAndListsThem()
    {
        var loadout = await CreateLoadout();
        var folder = FileSystem.Shared.GetKnownPath(KnownPath.TempDirectory).Combine($"{Guid.NewGuid()}");
        folder.CreateDirectory();
        File.WriteAllText((folder / "TexGenx64.exe").GetFullPath(), "exe");
        var vm = CreateViewModel(new FakeToolManager(), new FakeToolRunner(), new FakeFilePicker { FolderResult = [folder] },
            definitions: [Catalogue("texgen", "TexGen", "TexGenx64.exe")]);
        vm.LoadoutId = loadout.LoadoutId;

        await vm.AddToolFromPickerAsync(folder: true);

        vm.LastRunMessage.Should().Be("Added TexGen.");
        UserTool.FindByInstallation(Connection.Db, loadout.InstallationId).Select(t => t.Name).Should().Equal("TexGen");
        vm.UserTools.Should().BeEmpty("a catalogue tool is listed with the catalogue tools, not with the hand-added programs");
        folder.DeleteDirectory(recursive: true);
    }

    [Fact]
    public async Task AddingAToolFolderThatHoldsNothingKnownShowsTheRefusal()
    {
        var loadout = await CreateLoadout();
        var folder = FileSystem.Shared.GetKnownPath(KnownPath.TempDirectory).Combine($"{Guid.NewGuid()}");
        folder.CreateDirectory();
        var vm = CreateViewModel(new FakeToolManager(), new FakeToolRunner(), new FakeFilePicker { FolderResult = [folder] });
        vm.LoadoutId = loadout.LoadoutId;

        await vm.AddToolFromPickerAsync(folder: true);

        vm.LastRunMessage.Should().Contain("no known tool");
        folder.DeleteDirectory(recursive: true);
    }

    [Fact]
    public async Task AddingAToolDoesNothingWhenThePickerIsCancelled()
    {
        var loadout = await CreateLoadout();
        var vm = CreateViewModel(new FakeToolManager(), new FakeToolRunner(), new FakeFilePicker());
        vm.LoadoutId = loadout.LoadoutId;

        await vm.AddToolFromPickerAsync(folder: true);

        vm.LastRunMessage.Should().BeNull();
    }

    private sealed class ThrowingTool(string message, GameId gameId) : ITool
    {
        public IEnumerable<GameId> GameIds => [gameId];
        public string Name => "Throwing";

        public IJobTask<ITool, Unit> StartJob(Loadout.ReadOnly loadout, IJobMonitor monitor, CancellationToken cancellationToken)
            => throw new InvalidOperationException(message);
    }

    private sealed class StubTool(string name, GameId gameId) : ITool
    {
        public IEnumerable<GameId> GameIds => [gameId];
        public string Name => name;

        public IJobTask<ITool, Unit> StartJob(Loadout.ReadOnly loadout, IJobMonitor monitor, CancellationToken cancellationToken)
            => monitor.Begin<ITool, Unit>(this, _ => ValueTask.FromResult(Unit.Default));
    }

    /// <summary>
    /// Starts each tool's job without the synchronizer round trips of the real tool manager.
    /// </summary>
    private sealed class FakeToolManager : IToolManager
    {
        public IReadOnlyList<ITool> Tools { get; init; } = [];
        public List<ITool> Ran { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];

        /// <summary>Held by a run until the test completes it, so a test can look at the page mid-run.</summary>
        public Task? Gate { get; set; }

        /// <summary>Completes once a run has reached the manager, so a test can act on a run that really started.</summary>
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Func<ITool, (string? Group, int? ExitCode, int Captured)> Report { get; set; } = _ => (null, null, 0);

        public IEnumerable<ITool> GetTools(Loadout.ReadOnly loadout) => Tools;

        public async Task<Loadout.ReadOnly> RunTool(ITool tool, Loadout.ReadOnly loadout, IJobMonitor monitor, CancellationToken token = default)
            => (await RunToolWithOutcome(tool, loadout, monitor, token)).Loadout;

        public async Task<ToolRunOutcome> RunToolWithOutcome(ITool tool, Loadout.ReadOnly loadout, IJobMonitor monitor, CancellationToken token = default)
        {
            Ran.Add(tool);
            Tokens.Add(token);
            Entered.TrySetResult();
            if (Gate is { } gate) await gate.WaitAsync(token);
            await tool.StartJob(loadout, monitor, token);
            var (group, exit, captured) = Report(tool);
            return new ToolRunOutcome(loadout, group, exit, captured);
        }
    }

    private sealed class FakeToolRunner : IToolRunner
    {
        public sealed record Call(GameInstallation Installation, AbsolutePath Exe);

        public List<Call> Calls { get; } = [];
        public string? Diagnostic { get; init; }
        public string Strategy { get; init; } = "fake";

        public ToolRunPlan Plan(GameInstallation installation, AbsolutePath exe, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? env, ToolRunVia via = ToolRunVia.Auto)
            => Diagnostic is null
                ? ToolRunPlan.Runnable(Strategy, [exe.ToString()], exe.Parent, new Dictionary<string, string>())
                : ToolRunPlan.Failed(Diagnostic);

        public Task<ToolRunResult> RunWindowsTool(GameInstallation installation, AbsolutePath exe, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? env, bool logOutput, CancellationToken cancellationToken, ToolRunVia via = ToolRunVia.Auto)
        {
            if (Diagnostic is not null) throw new InvalidOperationException(Diagnostic);
            Calls.Add(new Call(installation, exe));
            return Task.FromResult(new ToolRunResult(0));
        }

        public List<PrefixCommand> PrefixCommands { get; } = [];

        public Task<PrefixRunResult> RunInPrefix(GameInstallation installation, PrefixCommand command, CancellationToken cancellationToken)
        {
            if (Diagnostic is not null) throw new InvalidOperationException(Diagnostic);
            PrefixCommands.Add(command);
            return Task.FromResult(new PrefixRunResult(0, "", ""));
        }

        public bool CanRun(GameInstallation installation, [NotNullWhen(false)] out string? diagnostic)
        {
            diagnostic = Diagnostic;
            return Diagnostic is null;
        }
    }

    private sealed class FakePrefixResolver : IPrefixResolver
    {
        public static AbsolutePath PrefixPath => FileSystem.Shared.GetKnownPath(KnownPath.TempDirectory).Combine("fake-prefix");

        public PrefixResolution Resolve(GameInstallation installation)
            => PrefixResolution.Resolved(new ResolvedPrefix(PrefixPath, CompatibilityLayerKind.SteamProton));
    }

    private sealed class FakeFilePicker : IAvaloniaInterop
    {
        public AbsolutePath[] Result { get; init; } = [];
        public AbsolutePath[] FolderResult { get; init; } = [];

        public Task<AbsolutePath[]> OpenFilePickerAsync(FilePickerOpenOptions filePickerOpenOptions) => Task.FromResult(Result);
        public Task<AbsolutePath[]> OpenFolderPickerAsync(FolderPickerOpenOptions folderPickerOpenOptions) => Task.FromResult(FolderResult);
        public void RegisterStorageProvider(IStorageProvider storageProvider) => throw new NotSupportedException();
        public void RegisterClipboard(IClipboard clipboardProvider) => throw new NotSupportedException();
        public Task SetClipboardTextAsync(string text) => throw new NotSupportedException();
    }
}
