using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using R3;
using SilverModManager.Abstractions.Games.Tools;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// A tool that writes into the game folder has its new and changed files moved out of Overrides into its own group
/// by <see cref="ToolManager"/>. Files that were in Overrides, or dropped into the game folder, before the tool
/// started are not its output.
/// </summary>
public class ToolGameFolderOutputTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<ToolGameFolderOutputTests>(helper)
{
    private const string GroupName = "Pandora Output";
    private static readonly GamePath B = new(LocationId.Game, "Data/meshes/b.nif");
    private static readonly GamePath C = new(LocationId.Game, "Data/meshes/c.nif");
    private static readonly GamePath Plugin = new(LocationId.Game, "Data/X.esp");

    private ToolOutputCapture Capture => ServiceProvider.GetRequiredService<ToolOutputCapture>();

    private static void Write(Loadout.ReadOnly loadout, GamePath path, string content)
    {
        var file = loadout.InstallationInstance.Locations.ToAbsolutePath(path);
        file.Parent.CreateDirectory();
        File.WriteAllText(file.GetFullPath(), content);
    }

    private Task Run(Loadout.ReadOnly loadout, ToolOutput output, string[]? ignored, Action<Loadout.ReadOnly> body)
    {
        var tool = new WritingTool(loadout.Game.GameId, output, ignored ?? [], body);
        var manager = new ToolManager(NullLogger<ToolManager>.Instance, [tool], [], SynchronizerService, Capture);
        return manager.RunTool(tool, loadout, ServiceProvider.GetRequiredService<IJobMonitor>());
    }

    private Task<ToolRunOutcome> RunForOutcome(Loadout.ReadOnly loadout, ToolOutput output, Action<Loadout.ReadOnly> body, ToolRunSummary? reported = null)
    {
        var tool = new WritingTool(loadout.Game.GameId, output, [], body) { LastRun = reported };
        var manager = new ToolManager(NullLogger<ToolManager>.Instance, [tool], [], SynchronizerService, Capture);
        return manager.RunToolWithOutcome(tool, loadout, ServiceProvider.GetRequiredService<IJobMonitor>());
    }

    private Task RunGameFolder(Loadout.ReadOnly loadout, Action<Loadout.ReadOnly> body, string[]? ignored = null) =>
        Run(loadout, new ToolOutput.GameFolder(GroupName), ignored, body);

    private static List<GamePath> PathsIn(IEnumerable<LoadoutItem.ReadOnly> children) =>
        children.Select(c => c.ToLoadoutItemWithTargetPath()).Select(c => (GamePath)c.TargetPath)
            .OrderBy(p => p.Path.ToString(), StringComparer.Ordinal).ToList();

    private LoadoutItemGroup.ReadOnly? Group(Loadout.ReadOnly loadout) =>
        LoadoutItem.FindByLoadout(Connection.Db, loadout.LoadoutId).OfTypeLoadoutItemGroup()
            .Where(g => g.AsLoadoutItem().Name == GroupName).Cast<LoadoutItemGroup.ReadOnly?>().SingleOrDefault();

    private List<GamePath> OutputPaths(Loadout.ReadOnly loadout) =>
        Group(loadout) is { } group ? PathsIn(group.Children.OfTypeLoadoutItemWithTargetPath().OfTypeLoadoutFile().Select(f => f.AsLoadoutItemWithTargetPath().AsLoadoutItem())) : [];

    private List<GamePath> OverridesPaths(Loadout.ReadOnly loadout)
    {
        var overrides = LoadoutOverridesGroup.FindByOverridesFor(Connection.Db, loadout.Id).Single();
        return PathsIn(overrides.AsLoadoutItemGroup().Children.OfTypeLoadoutItemWithTargetPath().OfTypeLoadoutFile().Select(f => f.AsLoadoutItemWithTargetPath().AsLoadoutItem()));
    }

    private string DiskText(Loadout.ReadOnly loadout, GamePath path) =>
        File.ReadAllText(loadout.InstallationInstance.Locations.ToAbsolutePath(path).GetFullPath());

    [Fact]
    public async Task TheOutcomeCountsTheFilesMovedIntoTheToolsGroup()
    {
        var loadout = await CreateLoadout();

        var outcome = await RunForOutcome(loadout, new ToolOutput.GameFolder(GroupName), l => { Write(l, B, "mesh"); Write(l, C, "mesh"); },
            new ToolRunSummary(0, CapturedFiles: 0));

        outcome.OutputGroup.Should().Be(GroupName);
        outcome.CapturedFiles.Should().Be(2);
        outcome.ExitCode.Should().Be(0);
        outcome.Loadout.LoadoutId.Should().Be(loadout.LoadoutId);
    }

    [Fact]
    public async Task TheOutcomeOfAToolWithoutOutputNamesNoGroupAndCapturesNothing()
    {
        var loadout = await CreateLoadout();

        var outcome = await RunForOutcome(loadout, ToolOutput.None, l => Write(l, B, "mesh"));

        outcome.OutputGroup.Should().BeNull();
        outcome.CapturedFiles.Should().Be(0);
        outcome.ExitCode.Should().BeNull("the tool reports no run summary");
    }

    [Fact]
    public async Task TheOutcomeOfADeclaredFolderToolCountsWhatTheToolCapturedItself()
    {
        var loadout = await CreateLoadout();

        var outcome = await RunForOutcome(loadout, new ToolOutput.DeclaredFolder("Lod", new GamePath(LocationId.Game, "Data")), _ => { },
            new ToolRunSummary(0, CapturedFiles: 7));

        outcome.OutputGroup.Should().Be("Lod");
        outcome.CapturedFiles.Should().Be(7);
    }

    [Fact]
    public async Task AFileTheToolWritesEndsUpInTheNamedGroupAndNotInOverrides()
    {
        var loadout = await CreateLoadout();

        await RunGameFolder(loadout, l => Write(l, B, "mesh"));

        loadout = Refresh(loadout);
        OutputPaths(loadout).Should().Equal(B);
        OverridesPaths(loadout).Should().NotContain(B);
        DiskText(loadout, B).Should().Be("mesh");
    }

    [Fact]
    public async Task AFileAlreadyInOverridesStaysThere()
    {
        var loadout = await CreateLoadout();
        Write(loadout, C, "user's own");
        loadout = await Synchronizer.Synchronize(loadout);
        OverridesPaths(Refresh(loadout)).Should().Contain(C, "the setup is that the file is an Overrides row before the run");

        await RunGameFolder(loadout, l => Write(l, B, "mesh"));

        loadout = Refresh(loadout);
        OverridesPaths(loadout).Should().Contain(C);
        OutputPaths(loadout).Should().Equal(B);
    }

    [Fact]
    public async Task AFileDroppedIntoTheGameFolderBeforeTheRunIsNotTheToolsOutput()
    {
        var loadout = await CreateLoadout();
        Write(loadout, C, "dropped by the user");

        await RunGameFolder(loadout, l => Write(l, B, "mesh"));

        loadout = Refresh(loadout);
        OverridesPaths(loadout).Should().Contain(C);
        OutputPaths(loadout).Should().Equal(B);
    }

    [Fact]
    public async Task ARewrittenModFileWinsOverTheModAfterAnotherSyncAndTheModKeepsItsOriginal()
    {
        var loadout = await CreateLoadout();
        LoadoutItemGroupId mod;
        using (var tx = Connection.BeginTransaction())
        {
            await AddModAsync(tx, [(RelativePath)"Data/X.esp"], loadout.LoadoutId, "Mod with a plugin");
            var result = await tx.Commit();
            mod = LoadoutItemGroupId.From(LoadoutItem.FindByLoadout(Connection.Db, loadout.LoadoutId).OfTypeLoadoutItemGroup().Single(g => g.AsLoadoutItem().Name == "Mod with a plugin").Id);
        }

        loadout = await Synchronizer.Synchronize(Refresh(loadout));
        DiskText(loadout, Plugin).Should().Be("Data/X.esp");

        await RunGameFolder(loadout, l => Write(l, Plugin, "cleaned"));

        loadout = await Synchronizer.Synchronize(Refresh(loadout));
        DiskText(loadout, Plugin).Should().Be("cleaned");
        OutputPaths(loadout).Should().Equal(Plugin);
        OverridesPaths(loadout).Should().NotContain(Plugin);
        var original = LoadoutItemGroup.Load(Connection.Db, mod.Value).Children.OfTypeLoadoutItemWithTargetPath().OfTypeLoadoutFile().Single();
        original.Hash.Should().Be(System.Text.Encoding.UTF8.GetBytes("Data/X.esp").AsSpan().xxHash3());
    }

    [Fact]
    public async Task ASecondRunMergesIntoTheGroupInsteadOfReplacingIt()
    {
        var loadout = await CreateLoadout();
        await RunGameFolder(loadout, l => Write(l, B, "first"));

        await RunGameFolder(Refresh(loadout), l => Write(l, C, "second"));

        loadout = await Synchronizer.Synchronize(Refresh(loadout));
        OutputPaths(loadout).Should().Equal(B, C);
        DiskText(loadout, B).Should().Be("first");
        DiskText(loadout, C).Should().Be("second");
    }

    [Fact]
    public async Task ASecondRunThatRewritesAFileReplacesThatRowOnly()
    {
        var loadout = await CreateLoadout();
        await RunGameFolder(loadout, l => { Write(l, B, "first"); Write(l, C, "keep"); });

        await RunGameFolder(Refresh(loadout), l => Write(l, B, "second"));

        loadout = await Synchronizer.Synchronize(Refresh(loadout));
        OutputPaths(loadout).Should().Equal([B, C], "one row per path");
        DiskText(loadout, B).Should().Be("second");
        DiskText(loadout, C).Should().Be("keep");
        OverridesPaths(loadout).Should().NotContain([B, C]);
    }

    [Fact]
    public async Task AnIgnoredPathStaysInOverrides()
    {
        var loadout = await CreateLoadout();
        var log = new GamePath(LocationId.Game, "Data/CalienteTools/BodySlide/BodySlide.log");

        await RunGameFolder(loadout, l => { Write(l, B, "mesh"); Write(l, log, "noise"); }, ignored: ["**/*.log"]);

        loadout = Refresh(loadout);
        OutputPaths(loadout).Should().Equal(B);
        OverridesPaths(loadout).Should().Contain(log);
    }

    [Fact]
    public async Task TheNewGroupRanksAboveEveryModInstalledBeforeTheRun()
    {
        var loadout = await CreateLoadout();
        using (var tx = Connection.BeginTransaction())
        {
            await AddModAsync(tx, [(RelativePath)"Data/other.nif"], loadout.LoadoutId, "Earlier mod");
            await tx.Commit();
        }
        var earlier = LoadoutItem.FindByLoadout(Connection.Db, loadout.LoadoutId).OfTypeLoadoutItemGroup().Single(g => g.AsLoadoutItem().Name == "Earlier mod").Id;
        using (var tx = Connection.BeginTransaction())
        {
            _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadout.LoadoutId, TargetId = LoadoutItemGroupId.From(earlier), Priority = ConflictPriority.From(7) };
            await tx.Commit();
        }

        await RunGameFolder(Refresh(loadout), l => Write(l, B, "mesh"));

        loadout = Refresh(loadout);
        var priorities = LoadoutItemGroupPriority.FindByLoadout(Connection.Db, loadout.LoadoutId).ToDictionary(r => r.TargetId.Value, r => r.Priority.Value);
        var output = Group(loadout)!.Value.Id;
        priorities[output].Should().BeGreaterThan(priorities[earlier]);
    }

    [Fact]
    public async Task AToolWithoutGameFolderOutputLeavesItsFilesInOverrides()
    {
        var loadout = await CreateLoadout();

        await Run(loadout, ToolOutput.None, null, l => Write(l, B, "mesh"));

        loadout = Refresh(loadout);
        Group(loadout).Should().BeNull();
        OverridesPaths(loadout).Should().Contain(B);
    }

    private sealed class WritingTool(GameId game, ToolOutput output, IReadOnlyList<string> ignored, Action<Loadout.ReadOnly> body) : ITool, IToolWithOutput, IToolWithRunSummary
    {
        public ToolRunSummary? LastRun { get; init; }

        public IEnumerable<GameId> GameIds => [game];
        public string Name => "Writing tool";
        public ToolOutput Output => output;
        public bool DisableOwnOutputDuringRun => false;
        public IReadOnlyList<string> IgnoredOutput => ignored;

        public IJobTask<ITool, Unit> StartJob(Loadout.ReadOnly loadout, IJobMonitor monitor, CancellationToken cancellationToken)
            => monitor.Begin<ITool, Unit>(this, _ =>
            {
                body(loadout);
                return ValueTask.FromResult(Unit.Default);
            });
    }
}
