using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using R3;
using SilverModManager.Abstractions.Games.Tools;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// A tool that regenerates all of its output must not see the previous run's files, and a run that fails must give
/// them back. <see cref="IToolWithOutput.DisableOwnOutputDuringRun"/> is what <see cref="ToolManager"/> acts on.
/// </summary>
public class ToolRunOutputTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<ToolRunOutputTests>(helper)
{
    private const string GroupName = "DynDOLOD Output";
    private static readonly GamePath Data = new(LocationId.Game, "Data");
    private static readonly GamePath Plugin = new(LocationId.Game, "Data/DynDOLOD.esp");

    private ToolOutputCapture Capture => ServiceProvider.GetRequiredService<ToolOutputCapture>();

    private AbsolutePath Folder(string content)
    {
        var folder = TemporaryFileManager.CreateFolder().Path;
        File.WriteAllText((folder / "DynDOLOD.esp").GetFullPath(), content);
        return folder;
    }

    private async Task<Loadout.ReadOnly> LoadoutWithAppliedOutput(string content)
    {
        var loadout = await CreateLoadout();
        await Capture.CaptureFolder(loadout, Folder(content), Data, GroupName, []);
        return await Synchronizer.Synchronize(Refresh(loadout));
    }

    private bool IsDisabled(Loadout.ReadOnly loadout) =>
        LoadoutItem.FindByLoadout(Connection.Db, loadout.LoadoutId).OfTypeLoadoutItemGroup().Single(g => g.AsLoadoutItem().Name == GroupName).AsLoadoutItem().IsDisabled;

    private ToolManager Manager(ITool tool) => new(NullLogger<ToolManager>.Instance, [tool], [], SynchronizerService, Capture);

    private Task Run(OutputTool tool, Loadout.ReadOnly loadout) =>
        Manager(tool).RunTool(tool, loadout, ServiceProvider.GetRequiredService<IJobMonitor>());

    [Fact]
    public async Task TheOwnGroupIsOffAndItsFilesAreOffDiskWhileTheToolRuns()
    {
        var loadout = await LoadoutWithAppliedOutput("first");
        var disk = loadout.InstallationInstance.Locations.ToAbsolutePath(Plugin);
        disk.FileExists.Should().BeTrue();
        bool? disabledInRun = null;
        bool? onDiskInRun = null;
        var tool = new OutputTool(loadout.Game.GameId, disableOwn: true, _ =>
        {
            disabledInRun = IsDisabled(loadout);
            onDiskInRun = disk.FileExists;
            return Task.CompletedTask;
        });

        await Run(tool, loadout);

        disabledInRun.Should().BeTrue();
        onDiskInRun.Should().BeFalse("the first sync takes the old output away before the tool starts");
    }

    [Fact]
    public async Task ASuccessfulRunLeavesTheReplacedGroupOn()
    {
        var loadout = await LoadoutWithAppliedOutput("first");
        var tool = new OutputTool(loadout.Game.GameId, disableOwn: true, async l =>
            await Capture.CaptureFolder(l, Folder("second"), Data, GroupName, []));

        await Run(tool, loadout);

        IsDisabled(loadout).Should().BeFalse();
        var disk = loadout.InstallationInstance.Locations.ToAbsolutePath(Plugin);
        (await disk.ReadAllTextAsync()).Should().Be("second");
    }

    [Fact]
    public async Task AFailedRunSwitchesTheOldOutputBackOn()
    {
        var loadout = await LoadoutWithAppliedOutput("first");
        var tool = new OutputTool(loadout.Game.GameId, disableOwn: true, _ => throw new InvalidOperationException("tool broke"));

        var act = () => Run(tool, loadout);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("tool broke");
        IsDisabled(loadout).Should().BeFalse();
        loadout = await Synchronizer.Synchronize(Refresh(loadout));
        (await loadout.InstallationInstance.Locations.ToAbsolutePath(Plugin).ReadAllTextAsync()).Should().Be("first");
    }

    [Fact]
    public async Task ACancelledRunSwitchesTheOldOutputBackOn()
    {
        var loadout = await LoadoutWithAppliedOutput("first");
        using var cts = new CancellationTokenSource();
        var tool = new OutputTool(loadout.Game.GameId, disableOwn: true, _ =>
        {
            cts.Cancel();
            cts.Token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });

        var act = () => Manager(tool).RunTool(tool, loadout, ServiceProvider.GetRequiredService<IJobMonitor>(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        IsDisabled(loadout).Should().BeFalse();
    }

    [Fact]
    public async Task AToolThatDoesNotAskLeavesItsGroupAlone()
    {
        var loadout = await LoadoutWithAppliedOutput("first");
        bool? disabledInRun = null;
        var tool = new OutputTool(loadout.Game.GameId, disableOwn: false, _ =>
        {
            disabledInRun = IsDisabled(loadout);
            return Task.CompletedTask;
        });

        await Run(tool, loadout);

        disabledInRun.Should().BeFalse();
    }

    [Fact]
    public async Task SetEnabledOnlyReportsAChange()
    {
        var loadout = await LoadoutWithAppliedOutput("first");

        (await Capture.SetEnabled(loadout.LoadoutId, GroupName, enabled: true)).Should().BeFalse("it is on already");
        (await Capture.SetEnabled(loadout.LoadoutId, GroupName, enabled: false)).Should().BeTrue();
        (await Capture.SetEnabled(loadout.LoadoutId, GroupName, enabled: false)).Should().BeFalse("it is off already");
        (await Capture.SetEnabled(loadout.LoadoutId, "No such group", enabled: false)).Should().BeFalse();
        IsDisabled(loadout).Should().BeTrue();
    }

    private sealed class OutputTool(GameId game, bool disableOwn, Func<Loadout.ReadOnly, Task> run) : ITool, IToolWithOutput
    {
        public IEnumerable<GameId> GameIds => [game];
        public string Name => "Output tool";
        public ToolOutput Output => new ToolOutput.DeclaredFolder(GroupName, Data);
        public bool DisableOwnOutputDuringRun => disableOwn;
        public IReadOnlyList<string> IgnoredOutput => [];

        public IJobTask<ITool, Unit> StartJob(Loadout.ReadOnly loadout, IJobMonitor monitor, CancellationToken cancellationToken)
            => monitor.Begin<ITool, Unit>(this, async _ =>
            {
                await run(loadout);
                return Unit.Default;
            });
    }
}
