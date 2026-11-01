using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Backend;
using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Networking.NexusWebApi;
using SilverModManager.Library.Tests.DownloadsService;
using SilverModManager.Library.Tests.Lock;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.Thunderstore;
using SilverModManager.Networking.Thunderstore.DTOs;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using Xunit;

namespace SilverModManager.Library.Tests.ModSources;

/// <summary>
/// <c>CheckUpdates</c> brings each source's idea of "newer version available" up to date. The Thunderstore half
/// is the call that feeds the checker, which nothing else in the app does.
/// </summary>
public class CheckUpdatesTests(IConnection connection)
{
    private sealed class FakeCatalogSource(bool stale, ThunderstoreCatalog catalog) : IThunderstoreCatalogSource
    {
        public List<string> Refreshed { get; } = [];
        public List<string> Loaded { get; } = [];

        public bool IsStale(string community) => stale;

        public Task Refresh(string community, CancellationToken cancellationToken)
        {
            Refreshed.Add(community);
            return Task.CompletedTask;
        }

        public Task<ThunderstoreCatalog> Load(string community, CancellationToken cancellationToken)
        {
            Loaded.Add(community);
            return Task.FromResult(catalog);
        }
    }

    private static string UniqueCommunity() => "community-" + Guid.NewGuid().ToString("N");

    private static IGameData ThunderstoreGameOf(string community)
    {
        var game = Substitute.For<IGameData, IThunderstoreGame>();
        ((IThunderstoreGame)game).ThunderstoreCommunity.Returns(community);
        return game;
    }

    private static ThunderstoreCatalog CatalogWith(string community, string team, string name, params string[] versions)
    {
        var records = versions
            .Select(version => new PackageVersion
            {
                Name = name,
                FullName = $"{team}-{name}-{version}",
                VersionNumber = version,
                DownloadUrl = $"https://thunderstore.io/package/download/{team}/{name}/{version}/",
            })
            .Reverse()
            .ToArray();
        return new ThunderstoreCatalog(community, [new PackageListing { Name = name, FullName = $"{team}-{name}", Owner = team, Versions = records }]);
    }

    private async Task<(ThunderstoreModSource Source, FakeCatalogSource Catalogs, ThunderstoreUpdateChecker Checker, IGameData Game, ThunderstoreLibraryFile.ReadOnly File)> Arrange(bool stale)
    {
        var community = UniqueCommunity();
        var team = "Team" + Guid.NewGuid().ToString("N");
        var file = await new LockFixtures(connection).AddThunderstoreFile(team, "Pkg", "1.0.0", "ts " + Guid.NewGuid(), null, community: community);
        var catalogs = new FakeCatalogSource(stale, CatalogWith(community, team, "Pkg", "1.0.0", "1.1.0"));
        var checker = new ThunderstoreUpdateChecker(connection);
        file.AsLibraryItem().TryGetAsLibraryFile(out var libraryFile).Should().BeTrue();
        libraryFile.TryGetAsThunderstoreLibraryFile(out var thunderstoreFile).Should().BeTrue();
        return (new ThunderstoreModSource(catalogs, checker), catalogs, checker, ThunderstoreGameOf(community), thunderstoreFile);
    }

    private static string? NewestVersionSeenBy(ThunderstoreUpdateChecker checker, ThunderstoreLibraryFile.ReadOnly file)
    {
        // Observe replays the last catalog, so a refresh that happened gives a value straight away.
        string? newest = null;
        using var _ = checker.Observe(file).Subscribe(update => newest = update.HasValue ? update.Value.Newest.Version.ToString() : null);
        return newest;
    }

    [Fact]
    public async Task ThunderstoreCheckUpdates_FeedsTheLoadedCatalogToTheChecker()
    {
        var (source, _, checker, game, file) = await Arrange(stale: false);

        await source.CheckUpdates(game, force: false, CancellationToken.None);

        NewestVersionSeenBy(checker, file).Should().Be("1.1.0");
    }

    [Fact]
    public async Task ThunderstoreCheckUpdates_RefreshesAStaleCatalogWithoutForce()
    {
        var (source, catalogs, _, game, _) = await Arrange(stale: true);

        await source.CheckUpdates(game, force: false, CancellationToken.None);

        catalogs.Refreshed.Should().HaveCount(1);
        catalogs.Loaded.Should().HaveCount(1);
    }

    [Fact]
    public async Task ThunderstoreCheckUpdates_LeavesAFreshCatalogAloneWithoutForce()
    {
        var (source, catalogs, _, game, _) = await Arrange(stale: false);

        await source.CheckUpdates(game, force: false, CancellationToken.None);

        catalogs.Refreshed.Should().BeEmpty();
        catalogs.Loaded.Should().HaveCount(1);
    }

    [Fact]
    public async Task ThunderstoreCheckUpdates_RefreshesAFreshCatalogWhenForced()
    {
        var (source, catalogs, _, game, _) = await Arrange(stale: false);

        await source.CheckUpdates(game, force: true, CancellationToken.None);

        catalogs.Refreshed.Should().HaveCount(1);
    }

    [Fact]
    public async Task ThunderstoreCheckUpdates_DoesNothingForAGameWithoutThunderstoreSupport()
    {
        var (source, catalogs, checker, _, file) = await Arrange(stale: true);

        await source.CheckUpdates(Substitute.For<IGameData>(), force: true, CancellationToken.None);

        catalogs.Refreshed.Should().BeEmpty();
        catalogs.Loaded.Should().BeEmpty();
        NewestVersionSeenBy(checker, file).Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NexusCheckUpdates_ChecksModPagesAndNotifiesWhateverForceIs(bool force)
    {
        var service = Substitute.For<IModUpdateService>();
        var source = new NexusModSource(service);
        using var cts = new CancellationTokenSource();

        await source.CheckUpdates(Substitute.For<IGameData>(), force, cts.Token);

        await service.Received(1).CheckAndUpdateModPages(cts.Token, true, true);
    }

    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            DIHelpers.ConfigureServices(services);
            services
                .AddLibraryModels()
                .AddGameInstallMetadataModel()
                .AddLoadoutAbstractions()
                .AddOSInterop()
                .AddFileExtractors();
        }
    }
}
