using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Games.TestFramework;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.MnemonicDB.Abstractions.IndexSegments;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;

namespace SilverModManager.DataModel.Tests.Deployment;

/// <summary>
/// Which roots the chain deploys by copy without asking a link backend: the saves and preferences roots,
/// where a game rewrites its own files in place, and whatever the game's own path filter rejects.
/// </summary>
public class ChainFilterTests(ITestOutputHelper helper) : AStubbedGameIsolatedTest<ChainFilterTests>(helper)
{
    private static readonly GamePath GameRoot = new(LocationId.Game, RelativePath.Empty);
    private static readonly GamePath PreferencesRoot = new(LocationId.Preferences, RelativePath.Empty);

    private IDeploymentBackend Copy => ServiceProvider.GetRequiredService<CopyDeploymentBackend>();

    private DeploymentChoice Choose(GameInstallation installation, GamePath root, params IDeploymentBackend[] backends)
        => new DeploymentBackendChain(backends).Choose(installation, root);

    private GameInstallation WithGameFilter(Func<GamePath, bool> filter)
    {
        var synchronizer = Substitute.For<ILoadoutSynchronizer>();
        synchronizer.GamePathFilter.Returns(GamePathFilters.Create(filter));
        var game = new FilteredStubbedGame(ServiceProvider, synchronizer);
        var original = GameInstallation.LocatorResult;
        var result = new GameLocatorResult
        {
            Game = game,
            Path = original.Path,
            Store = original.Store,
            StoreIdentifier = original.StoreIdentifier,
            LocatorIds = original.LocatorIds,
            Locator = original.Locator,
        };
        return new GameInstallation(result, GameInstallation.Locations);
    }

    [Fact]
    public void APreferencesRootIsCopyWithoutAProbe()
    {
        // A hardlinked INI that the game rewrites in place would rewrite the file store's copy as well.
        var reflink = new AcceptingBackend(DeploymentBackendId.Reflink);

        var choice = Choose(GameInstallation, PreferencesRoot, Copy, reflink);

        choice.Backend.Should().Be(DeploymentBackendId.Copy);
        choice.Reason.Should().Be("the game rewrites files here in place");
        reflink.PreflightCalls.Should().BeEmpty();
    }

    [Fact]
    public void ARootTheGamesOwnFilterRejectsIsCopyWithoutAProbe()
    {
        var reflink = new AcceptingBackend(DeploymentBackendId.Reflink);

        var choice = Choose(WithGameFilter(p => p == GameRoot), GameRoot, Copy, reflink);

        choice.Backend.Should().Be(DeploymentBackendId.Copy);
        choice.Reason.Should().Be("location is never written");
        reflink.PreflightCalls.Should().BeEmpty();
    }

    [Fact]
    public void ARootTheGamesOwnFilterAcceptsStillGoesThroughTheBackendOrder()
    {
        var reflink = new AcceptingBackend(DeploymentBackendId.Reflink);

        var choice = Choose(WithGameFilter(_ => false), GameRoot, Copy, reflink);

        choice.Backend.Should().Be(DeploymentBackendId.Reflink);
        reflink.PreflightCalls.Should().ContainSingle();
    }

    /// <summary>
    /// The stub, with a synchronizer the test controls. Interface re-implementation is enough: the chain reaches
    /// the synchronizer through <see cref="IHasSynchronizer"/>.
    /// </summary>
    private sealed class FilteredStubbedGame(IServiceProvider provider, ILoadoutSynchronizer synchronizer) : StubbedGame(provider), IHasSynchronizer
    {
        ILoadoutSynchronizer IHasSynchronizer.Synchronizer => synchronizer;
    }

    private sealed class AcceptingBackend(DeploymentBackendId id) : IDeploymentBackend
    {
        public List<GamePath> PreflightCalls { get; } = [];

        public DeploymentBackendId Id => id;
        public bool UsesLooseStaging => false;

        public DeploymentPreflight Preflight(GameInstallation installation, GamePath root)
        {
            PreflightCalls.Add(root);
            return DeploymentPreflight.Accept("accepted");
        }

        public IReadOnlyList<AbsolutePath> RequiredFilesystems(GameInstallation installation, GamePath root) => [];

        public Task Backup(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => throw new NotSupportedException();
        public void Ingest(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => throw new NotSupportedException();
        public Task AdaptLoadout(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => throw new NotSupportedException();
        public IReadOnlyCollection<GamePath> Delete(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => throw new NotSupportedException();
        public Task Extract(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => throw new NotSupportedException();
        public Task WriteIntrinsics(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => throw new NotSupportedException();
        public void AddReifiedDeletes(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => throw new NotSupportedException();
        public void CleanDirectories(DeploymentContext context, IReadOnlyCollection<GamePath> directoriesWithDeletions, Entities<DiskStateEntry.ReadOnly> newDiskState) => throw new NotSupportedException();
    }
}
