using System.Diagnostics.CodeAnalysis;
using Avalonia.Platform.Storage;
using Avalonia.Input.Platform;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Abstractions.Loadouts;
using NexusMods.App.UI;
using NexusMods.App.UI.Pages.Tools;
using NexusMods.App.UI.Windows;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Loadouts;
using R3;
using Unit = R3.Unit;

namespace NexusMods.UI.Tests.Pages;

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

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*No Wine prefix was found.*");
        toolManager.Ran.Should().BeEmpty();
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

        public ToolRunPlan Plan(GameInstallation installation, AbsolutePath exe, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? env)
            => Diagnostic is null
                ? ToolRunPlan.Runnable(Strategy, [exe.ToString()], exe.Parent, new Dictionary<string, string>())
                : ToolRunPlan.Failed(Diagnostic);

        public Task<ToolRunResult> RunWindowsTool(GameInstallation installation, AbsolutePath exe, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? env, bool logOutput, CancellationToken cancellationToken)
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
        public void RegisterStorageProvider(IStorageProvider storageProvider) => throw new NotSupportedException();
        public void RegisterClipboard(IClipboard clipboardProvider) => throw new NotSupportedException();
        public Task SetClipboardTextAsync(string text) => throw new NotSupportedException();
    }
}
