using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts.Deployment;
using NexusMods.Abstractions.Loadouts.Synchronizers.Deployment;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Platform.Proton.Launch;
using NexusMods.Platform.Proton.Verbs;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.IO;
using NexusMods.Sdk.Native;
using NexusMods.Sdk.ProxyConsole;
using NSubstitute;

namespace NexusMods.Platform.Proton.Tests.Verbs;

/// <summary>
/// The temp directory is tmpfs on the dev box, where hard links work and FICLONE does not. The selection chain is
/// replaced by one the test steers, so what it picks does not depend on where the checkout lives.
/// </summary>
public class DeploymentVerbsTests(ITestOutputHelper helper) : AStubbedGameIsolatedTest<DeploymentVerbsTests>(helper)
{
    private const int TimeoutMs = 60000;

    private static readonly AbsolutePath Folder = NexusMods.Paths.FileSystem.Shared.GetKnownPath(KnownPath.TempDirectory).Combine($"deployment-verbs-{Guid.NewGuid():N}");
    private static readonly GamePath GameRoot = new(LocationId.Game, RelativePath.Empty);
    private static readonly GamePath OtherRoot = new(LocationId.Game, "other");
    private static readonly GamePath FileA = new(LocationId.Game, "mods/a.txt");

    // Every test builds its own game folder under this one. The hosts shut down in the background, so the folder
    // goes when the test process exits instead of when a test ends.
    static DeploymentVerbsTests() => AppDomain.CurrentDomain.ProcessExit += (_, _) =>
    {
        if (Directory.Exists(Folder.ToString())) Directory.Delete(Folder.ToString(), recursive: true);
    };

    protected override AbsolutePath TemporaryFilesPrefix => Folder;

    private sealed class SteerableSelector : IDeploymentBackendSelector
    {
        public DeploymentBackendId Backend { get; set; } = DeploymentBackendId.Copy;

        public DeploymentChoice Choose(GameInstallation installation, GamePath root) => new(Backend, "steered by the test");
    }

    private sealed class RecordingRenderer : IRenderer
    {
        public List<string> Lines { get; } = [];

        public string Text => string.Join('\n', Lines);

        public ValueTask RenderAsync(IRenderable renderable)
        {
            if (renderable is Text text) Lines.AddRange(text.Template.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            return ValueTask.CompletedTask;
        }

        public ValueTask ClearAsync() => ValueTask.CompletedTask;
    }

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        services.AddSingleton<SteerableSelector>();
        return base.AddServices(services).AddSingleton<IDeploymentBackendSelector>(s => s.GetRequiredService<SteerableSelector>());
    }

    private SteerableSelector Selector => ServiceProvider.GetRequiredService<SteerableSelector>();
    private IDeploymentPlanStore Plans => ServiceProvider.GetRequiredService<IDeploymentPlanStore>();

    private DeploymentCommand Command() => new(
        SynchronizerService,
        new LoadoutResolver(Connection, GameRegistry, Substitute.For<IEnvironmentReader>()),
        GameRegistry,
        Connection,
        Plans,
        Selector,
        ServiceProvider.GetServices<IDeploymentBackend>(),
        ServiceProvider.GetRequiredService<IGameLaunchSession>(),
        ServiceProvider.GetRequiredService<ILooseStagingStore>());

    private async Task<(int ExitCode, RecordingRenderer Output)> Run(string? root = null, string? use = null, bool recommended = false, bool json = false, string? game = null)
    {
        var renderer = new RecordingRenderer();
        var exitCode = await Command().RunAsync(renderer, game ?? GameInstallation.Game.DisplayName, root, use, recommended, json);
        return (exitCode, renderer);
    }

    private DeploymentPlan Plan() => Plans.Load(Connection.Db, GameRegistry.ForceGetMetadata(GameInstallation).GameInstallMetadataId);

    private DeploymentPlanRoot Row(GamePath root) => Plan().Roots.Single(r => r.Root == root);

    private async Task ApplyLoadoutWithFile()
    {
        await LoadoutManager.ManageInstallation(GameInstallation);
        var loadout = await CreateLoadout();

        using (var tx = Connection.BeginTransaction())
        {
            var group = AddEmptyGroup(tx, loadout.LoadoutId, "Mod");
            AddFile(tx, loadout.LoadoutId, group, FileA, "a content", out var hash, out var size);
            await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(FileA.FileName, new MemoryStream(Encoding.UTF8.GetBytes("a content"))), hash, size)]);
            await tx.Commit();
        }

        await Synchronizer.Synchronize(loadout);
    }

    private LibcStat.Buffer Stat(GamePath path)
    {
        LibcStat.TryStat(GameInstallation.Locations.ToAbsolutePath(path).GetFullPath(), out var buffer).Should().Be(0);
        return buffer;
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task UseHardlink_ResetsWithCopyThenAppliesByHardlink()
    {
        await ApplyLoadoutWithFile();
        Stat(FileA).NLink.Should().Be(1, "copy deploys an independent file");

        var (exitCode, output) = await Run(root: "Game", use: "hardlink");

        exitCode.Should().Be(0, output.Text);
        Row(GameRoot).Should().Match<DeploymentPlanRoot>(r => r.Backend == DeploymentBackendId.Hardlink && r.Chosen);
        Stat(FileA).NLink.Should().Be(2, "the deployed file now shares its inode with the store");
        File.ReadAllText(GameInstallation.Locations.ToAbsolutePath(FileA).GetFullPath()).Should().Be("a content");
        output.Text.Should().Contain("Game: copy -> hardlink");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task UseWithoutARootMovesEveryRoot()
    {
        await ApplyLoadoutWithFile();

        var (exitCode, output) = await Run(use: "hardlink");

        exitCode.Should().Be(0, output.Text);
        Plan().Roots.Should().OnlyContain(r => r.Backend == DeploymentBackendId.Hardlink && r.Chosen);
        Plan().Roots.Should().HaveCount(GameInstallation.Locations.Count);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task Recommended_MovesOnlyRootsThatDiffer()
    {
        await ApplyLoadoutWithFile();
        using (var tx = Connection.BeginTransaction())
        {
            Plans.Write(tx, Connection.Db, GameRegistry.ForceGetMetadata(GameInstallation).GameInstallMetadataId, OtherRoot, DeploymentBackendId.Hardlink, chosen: false);
            await tx.Commit();
        }
        Selector.Backend = DeploymentBackendId.Hardlink;

        var (exitCode, output) = await Run(recommended: true);

        exitCode.Should().Be(0, output.Text);
        Row(GameRoot).Should().Match<DeploymentPlanRoot>(r => r.Backend == DeploymentBackendId.Hardlink && !r.Chosen, "the chain's own pick leaves the row unchosen");
        output.Text.Should().Contain("Game: copy -> hardlink (steered by the test)");
        output.Text.Should().Contain("Game/other: hardlink, already what the chain picks");
        Stat(FileA).NLink.Should().Be(2);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task Recommended_LeavesAChosenRootAlone()
    {
        await ApplyLoadoutWithFile();
        (await Run(root: "Game", use: "copy")).ExitCode.Should().Be(0);
        Row(GameRoot).Chosen.Should().BeTrue("a person who picks the backend the root already has still pins it");
        Selector.Backend = DeploymentBackendId.Hardlink;

        var (exitCode, output) = await Run(root: "Game", recommended: true);

        exitCode.Should().Be(0, output.Text);
        Row(GameRoot).Backend.Should().Be(DeploymentBackendId.Copy);
        Stat(FileA).NLink.Should().Be(1);
        output.Text.Should().Contain("Game: copy, chosen, left as is");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task UnknownBackendIdIsRefusedNamingTheRegisteredOnes()
    {
        await ApplyLoadoutWithFile();

        var (exitCode, output) = await Run(use: "teleport");

        exitCode.Should().Be(LaunchSession.FailureExitCode);
        output.Text.Should().Contain("teleport");
        foreach (var id in new[] { "copy", "hardlink", "reflink", "symlink" }) output.Text.Should().Contain(id);
        Plan().Roots.Should().OnlyContain(r => r.Backend == DeploymentBackendId.Copy);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task RefusedWhileTheGameRuns()
    {
        await ApplyLoadoutWithFile();
        ServiceProvider.GetRequiredService<FakeGameLaunchSession>().Running = true;

        var (exitCode, output) = await Run(use: "hardlink");

        exitCode.Should().Be(LaunchSession.FailureExitCode);
        output.Text.Should().Contain("is running from SilverModManager");
        Plan().Roots.Should().OnlyContain(r => r.Backend == DeploymentBackendId.Copy);
        Stat(FileA).NLink.Should().Be(1);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task ExactlyOneOfUseAndRecommendedIsRequired()
    {
        await ApplyLoadoutWithFile();

        var neither = await Run();
        var both = await Run(use: "copy", recommended: true);

        neither.ExitCode.Should().Be(LaunchSession.FailureExitCode);
        neither.Output.Text.Should().Contain("--use").And.Contain("--recommended");
        both.ExitCode.Should().Be(LaunchSession.FailureExitCode);
        both.Output.Text.Should().Contain("--use").And.Contain("--recommended");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task UnknownRootIsRefusedNamingTheKnownOnes()
    {
        await ApplyLoadoutWithFile();

        var (exitCode, output) = await Run(root: "Nowhere", use: "hardlink");

        exitCode.Should().Be(LaunchSession.FailureExitCode);
        output.Text.Should().Contain("Nowhere").And.Contain("Game");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task UnknownGameIsRefused()
    {
        var (exitCode, output) = await Run(use: "hardlink", game: "No Such Game");

        exitCode.Should().Be(LaunchSession.FailureExitCode);
        output.Text.Should().Contain("No Such Game");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task TheVerbPrintsThePlanAfterTheSwitchInTheDoctorsFormat()
    {
        await ApplyLoadoutWithFile();

        var (_, output) = await Run(root: "Game", use: "hardlink");

        output.Text.Should().Contain("Deployment:").And.Contain("  Game -> hardlink (chosen)");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task JsonListsEachRootsBeforeAndAfter()
    {
        await ApplyLoadoutWithFile();

        var (exitCode, output) = await Run(root: "Game", use: "hardlink", json: true);

        exitCode.Should().Be(0, output.Text);
        using var document = JsonDocument.Parse(output.Text);
        var root = document.RootElement;
        root.GetProperty("game").GetString().Should().Be(GameInstallation.Game.DisplayName);
        var moved = root.GetProperty("roots").EnumerateArray().Single();
        moved.GetProperty("root").GetString().Should().Be("Game");
        moved.GetProperty("before").GetString().Should().Be("copy");
        moved.GetProperty("after").GetString().Should().Be("hardlink");
        moved.GetProperty("outcome").GetString().Should().Be("moved");
        root.GetProperty("deployment").EnumerateArray().Should().Contain(d => d.GetProperty("root").GetString() == "Game" && d.GetProperty("backend").GetString() == "hardlink");
    }
}
