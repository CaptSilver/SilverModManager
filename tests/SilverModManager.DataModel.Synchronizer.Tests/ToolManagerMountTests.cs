using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NSubstitute;
using R3;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.Tools;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Deployment.Vfs;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// A tool run holds the mounts of the loadout's mounting roots from after the apply until the tool is done, so the tool
/// sees the loadout and what it writes is promoted before its output is collected.
/// </summary>
public class ToolManagerMountTests(ITestOutputHelper helper) : AStubbedGameTest<ToolManagerMountTests>(helper)
{
    private const string GroupName = "Tool Output";
    private static readonly GamePath DataRoot = new(LocationId.Game, "Data");
    private static readonly GamePath Produced = DataRoot / "produced.txt";

    private readonly List<string> _events = [];

    protected override IServiceCollection AddServices(IServiceCollection services) =>
        base.AddServices(services).AddRecordingMountingDeploymentBackend();

    private RecordingMountingDeploymentBackend Mounting => ServiceProvider.GetRequiredService<RecordingMountingDeploymentBackend>();

    private ISynchronizerService RecordingSync()
    {
        var sync = Substitute.For<ISynchronizerService>();
        sync.Synchronize(Arg.Any<LoadoutId>(), Arg.Any<BackupOverflowChoice?>(), Arg.Any<CancellationToken>())
            .Returns(_ => { _events.Add("sync"); return Task.CompletedTask; });
        return sync;
    }

    private ToolManager NewManager(ITool tool) => new(
        NullLogger<ToolManager>.Instance, [tool], [], RecordingSync(),
        ServiceProvider.GetRequiredService<ToolOutputCapture>(),
        ServiceProvider.GetRequiredService<IDeploymentMountScopeFactory>());

    private async Task<Loadout.ReadOnly> LoadoutWithAMountingRoot()
    {
        await LoadoutManager.ManageInstallation(GameInstallation);
        var id = GameRegistry.ForceGetMetadata(GameInstallation).GameInstallMetadataId;
        using var tx = Connection.BeginTransaction();
        ServiceProvider.GetRequiredService<IDeploymentPlanStore>().Write(tx, Connection.Db, id, DataRoot, RecordingMountingDeploymentBackend.BackendId, chosen: true);
        await tx.Commit();
        return await CreateLoadout();
    }

    private Task Run(ITool tool, Loadout.ReadOnly loadout) =>
        NewManager(tool).RunTool(tool, loadout, ServiceProvider.GetRequiredService<IJobMonitor>());

    [Fact]
    public async Task MountsAfterTheFirstApplyAndFinishesBeforeTheSecond()
    {
        var loadout = await LoadoutWithAMountingRoot();
        Mounting.OnFinish = _ => { _events.Add("finish"); return Task.CompletedTask; };
        var tool = new RecordingTool(loadout.Game.GameId, ToolOutput.None, _ => _events.Add(Mounting.Calls.Any(c => c.Phase == "Mount") ? "tool (mounted)" : "tool (not mounted)"));

        await Run(tool, loadout);

        _events.Should().Equal("sync", "tool (mounted)", "finish", "sync");
    }

    [Fact]
    public async Task APlanWithoutAMountingRootMountsNothing()
    {
        await LoadoutManager.ManageInstallation(GameInstallation);
        var loadout = await CreateLoadout();
        var tool = new RecordingTool(loadout.Game.GameId, ToolOutput.None, _ => _events.Add("tool"));

        await Run(tool, loadout);

        Mounting.Calls.Should().NotContain(c => c.Phase == "Mount" || c.Phase == "Finish");
        _events.Should().Equal("sync", "tool", "sync");
    }

    [Fact]
    public async Task PromotedFilesLandInTheToolsGroup()
    {
        var loadout = await LoadoutWithAMountingRoot();
        var sink = ServiceProvider.GetRequiredService<IPromotionSink>();
        Mounting.OnFinish = async root =>
        {
            // What a real mount's promotion does: the bytes into the store, the file into the Overrides group.
            var bytes = Encoding.UTF8.GetBytes("made by the tool");
            var identity = FileIdentity.XxHash3(bytes.xxHash3());
            var size = Size.FromLong(bytes.Length);
            await sink.Backup([(identity, size, new MemoryStreamFactory("produced.txt", new MemoryStream(bytes)))]);
            await sink.Record(loadout.LoadoutId, root, [new PromotedFile(RelativePath.FromUnsanitizedInput("produced.txt"), identity, size)], []);
        };
        var tool = new RecordingTool(loadout.Game.GameId, new ToolOutput.GameFolder(GroupName), _ => { });

        await Run(tool, loadout);

        var group = LoadoutItem.FindByLoadout(Connection.Db, loadout.LoadoutId).OfTypeLoadoutItemGroup().Single(g => g.AsLoadoutItem().Name == GroupName);
        group.Children.OfTypeLoadoutItemWithTargetPath().Select(f => (GamePath)f.TargetPath).Should().Equal(Produced);
    }

    [Fact]
    public async Task AFailingToolStillFinishesTheScope()
    {
        var loadout = await LoadoutWithAMountingRoot();
        var tool = new RecordingTool(loadout.Game.GameId, ToolOutput.None, _ => throw new InvalidOperationException("the tool broke"));

        var act = () => Run(tool, loadout);

        await act.Should().ThrowAsync<Exception>().WithMessage("*the tool broke*");
        Mounting.Calls.Should().Contain(("Finish", DataRoot));
        ServiceProvider.GetRequiredService<HeldMounts>().Any.Should().BeFalse();
    }

    [Fact]
    public async Task AToolThatLeavesFilesOpenUnderAMountIsReportedAfterTheIngest()
    {
        var loadout = await LoadoutWithAMountingRoot();
        Mounting.FinishResults[DataRoot] = new MountFinishResult(MountFinishStatus.StillInUse, []);
        var tool = new RecordingTool(loadout.Game.GameId, ToolOutput.None, _ => _events.Add("tool"));

        var act = () => Run(tool, loadout);

        var thrown = (await act.Should().ThrowAsync<MountStillInUseException>()).Which;
        thrown.Message.Should().Be("Recording tool still has files open under Game/Data; its changes are promoted the next time the app applies or launches");
        _events.Should().Equal("sync", "tool", "sync");
    }

    [Fact]
    public async Task AGameToolIsLeftToItsLaunchSessionWhichMountsForItself()
    {
        var loadout = await LoadoutWithAMountingRoot();
        var tool = new GameTool(loadout.Game.GameId, _ => _events.Add("tool"));

        await Run(tool, loadout);

        _events.Should().Contain("tool");
        Mounting.Calls.Should().NotContain(c => c.Phase == "Mount");
    }

    private sealed class GameTool(GameId game, Action<Loadout.ReadOnly> body) : RecordingTool(game, ToolOutput.None, body), IRunGameTool
    {
        public ValueTask<LaunchSpec> GetLaunchSpec(Loadout.ReadOnly loadout, CancellationToken cancellationToken) => throw new NotSupportedException();
        public IObservable<bool> WaitingForSteam => Observable.Return(false).AsSystemObservable();
        public IObservable<bool> RunningInstallStep => Observable.Return(false).AsSystemObservable();
    }

    private class RecordingTool(GameId game, ToolOutput output, Action<Loadout.ReadOnly> body) : ITool, IToolWithOutput
    {
        public IEnumerable<GameId> GameIds => [game];
        public string Name => "Recording tool";
        public ToolOutput Output => output;
        public bool DisableOwnOutputDuringRun => false;
        public IReadOnlyList<string> IgnoredOutput => [];

        public IJobTask<ITool, Unit> StartJob(Loadout.ReadOnly loadout, IJobMonitor monitor, CancellationToken cancellationToken)
            => monitor.Begin<ITool, Unit>(this, _ =>
            {
                body(loadout);
                return ValueTask.FromResult(Unit.Default);
            });
    }
}
