using System.Diagnostics.CodeAnalysis;
using Avalonia.Platform.Storage;
using Avalonia.Input.Platform;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI;
using SilverModManager.App.UI.Pages.Tools;
using SilverModManager.App.UI.Windows;
using NexusMods.Paths;
using SilverModManager.Sdk;
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

    private ToolsPageViewModel CreateViewModel(FakeToolManager toolManager, FakeToolRunner toolRunner, FakeFilePicker picker)
    {
        return new ToolsPageViewModel(
            Provider.GetRequiredService<IWindowManager>(),
            NullLogger<ToolsPageViewModel>.Instance,
            Connection,
            toolManager,
            toolRunner,
            new FakePrefixResolver(),
            picker,
            Provider.GetRequiredService<IJobMonitor>()
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

        toolManager.Ran.Should().ContainSingle().Which.Should().BeOfType<WindowsProgramTool>().Which.Exe.Should().Be(exe);
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

        public IEnumerable<ITool> GetTools(Loadout.ReadOnly loadout) => Tools;

        public async Task<Loadout.ReadOnly> RunTool(ITool tool, Loadout.ReadOnly loadout, IJobMonitor monitor, CancellationToken token = default)
        {
            Ran.Add(tool);
            await tool.StartJob(loadout, monitor, token);
            return loadout;
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

        public Task<AbsolutePath[]> OpenFilePickerAsync(FilePickerOpenOptions filePickerOpenOptions) => Task.FromResult(Result);
        public Task<AbsolutePath[]> OpenFolderPickerAsync(FolderPickerOpenOptions folderPickerOpenOptions) => throw new NotSupportedException();
        public void RegisterStorageProvider(IStorageProvider storageProvider) => throw new NotSupportedException();
        public void RegisterClipboard(IClipboard clipboardProvider) => throw new NotSupportedException();
        public Task SetClipboardTextAsync(string text) => throw new NotSupportedException();
    }
}
