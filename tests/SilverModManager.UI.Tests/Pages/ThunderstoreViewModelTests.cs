using SilverModManager.App.UI.Controls.Cards;
using System.Reactive.Linq;
using System.Collections.Immutable;
using System.Net.Http;
using DynamicData.Kernel;
using SilverModManager.Abstractions.Games;
using NexusMods.Paths;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.NexusModsApi;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Pages.Thunderstore;
using SilverModManager.App.UI.Settings;
using SilverModManager.App.UI.Windows;
using SilverModManager.Networking.Thunderstore;
using SilverModManager.Networking.Thunderstore.DTOs;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk;
using System.Security.Cryptography;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Thunderstore;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;
using NSubstitute;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests.Pages;

public class ThunderstoreViewModelTests(IServiceProvider provider) : AUiTest(provider)
{
    private sealed class FakeCatalogSource(ThunderstoreCatalog? catalog) : IThunderstoreCatalogSource
    {
        public bool Stale { get; set; }
        public Exception? RefreshFailure { get; set; }
        public Exception? LoadFailure { get; set; }
        public Exception? StaleFailure { get; set; }
        public int Refreshes { get; private set; }
        public List<string> Communities { get; } = [];

        public bool IsStale(string community) => StaleFailure is null ? Stale : throw StaleFailure;

        public Task Refresh(string community, CancellationToken cancellationToken)
        {
            Refreshes++;
            return RefreshFailure is null ? Task.CompletedTask : Task.FromException(RefreshFailure);
        }

        public Task<ThunderstoreCatalog> Load(string community, CancellationToken cancellationToken)
        {
            Communities.Add(community);
            if (LoadFailure is not null) return Task.FromException<ThunderstoreCatalog>(LoadFailure);
            return Task.FromResult(catalog ?? new ThunderstoreCatalog(community, []));
        }
    }

    private sealed class FakeInstallSource : IThunderstoreSource
    {
        public List<ThunderstoreInstallRequest> Requests { get; } = [];
        public IReadOnlyList<LibraryFile.ReadOnly> Files { get; set; } = [];
        public Exception? Failure { get; set; }

        public ValueTask<IReadOnlyList<LibraryFile.ReadOnly>> AddAsync(ThunderstoreInstallRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Failure is null ? ValueTask.FromResult(Files) : ValueTask.FromException<IReadOnlyList<LibraryFile.ReadOnly>>(Failure);
        }
    }

    private static ILoadoutManager LoadoutManagerThatInstalls()
    {
        var manager = Substitute.For<ILoadoutManager>();
        manager.InstallItem(default, default, default, default, default).ReturnsForAnyArgs(_ =>
        {
            var task = Substitute.For<IJobTask<IInstallLoadoutItemJob, InstallLoadoutItemJobResult>>();
            task.GetAwaiter().Returns(Task.FromResult(default(InstallLoadoutItemJobResult)).GetAwaiter());
            return task;
        });
        return manager;
    }

    private async Task<LibraryFile.ReadOnly> AddLibraryFile(string team, string name, string version)
    {
        var fileName = $"{team}-{name}-{version}.zip";
        var bytes = System.Text.Encoding.UTF8.GetBytes(fileName);
        using var tx = Connection.BeginTransaction();
        var file = new LibraryFile.New(tx, out var id)
        {
            FileName = fileName,
            Hash = bytes.xxHash3(),
            Size = Size.From((ulong)bytes.Length),
            LibraryItem = new LibraryItem.New(tx, id) { Name = fileName },
        };
        _ = new ThunderstoreLibraryFile.New(tx, id)
        {
            LibraryFile = file,
            Namespace = team,
            Name = name,
            VersionNumber = version,
            Community = "repo",
            PinnedSha256 = Sha256Value.From(SHA256.HashData(bytes)),
        };
        var result = await tx.Commit();
        return result.Remap(file);
    }

    private async Task LinkIntoLoadout(LoadoutId loadoutId, LibraryFile.ReadOnly file)
    {
        using var tx = Connection.BeginTransaction();
        var group = new LoadoutItemGroup.New(tx, out var id)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, id) { Name = file.FileName.ToString(), LoadoutId = loadoutId },
        };
        _ = new LibraryLinkedLoadoutItem.New(tx, id) { LibraryItemId = file.Id, LoadoutItemGroup = group };
        await tx.Commit();
    }

    private static PackageListing Package(string team, string name, double rating, bool deprecated = false, string version = "1.0.0", string[]? categories = null, string? icon = null, long downloads = 0, bool pinned = false, DateTimeOffset? created = null, DateTimeOffset? updated = null)
        => new()
        {
            Name = name,
            FullName = $"{team}-{name}",
            Owner = team,
            PackageUrl = $"https://thunderstore.io/c/repo/p/{team}/{name}/",
            RatingScore = rating,
            IsDeprecated = deprecated,
            IsPinned = pinned,
            DateCreated = created,
            DateUpdated = updated,
            Categories = categories ?? [],
            Versions =
            [
                new PackageVersion
                {
                    Name = name,
                    FullName = $"{team}-{name}-{version}",
                    VersionNumber = version,
                    Description = $"{name} does things",
                    Icon = icon,
                    Downloads = downloads,
                    DownloadUrl = $"https://thunderstore.io/package/download/{team}/{name}/{version}/",
                },
            ],
        };

    private static ThunderstoreCatalog Catalog() => new("repo",
    [
        Package("Zehs", "REPOLib", rating: 900, version: "4.2.0"),
        Package("nickklmao", "REPOConfig", rating: 300),
        Package("BepInEx", "BepInExPack", rating: 1500, version: "5.4.2305"),
        Package("Old", "Abandoned", rating: 5000, deprecated: true),
        Package("Packs", "BigPack", rating: 10, categories: ["Modpacks"]),
    ]);

    // A loadout row of a game the registry does not know would make every other test's loadout observers throw, so the
    // row is the located test game's and this wrapper stands in for the Thunderstore side of it.
    private sealed class ThunderstoreGameOfLoadout(IGameData inner, IThunderstoreGame thunderstore) : IGameData, IThunderstoreGame
    {
        public GameId GameId => inner.GameId;
        public StoreIdentifiers StoreIdentifiers => inner.StoreIdentifiers;
        public string DisplayName => inner.DisplayName;
        public Optional<NexusModsGameId> NexusModsGameId => inner.NexusModsGameId;
        public IStreamFactory IconImage => inner.IconImage;
        public IStreamFactory TileImage => inner.TileImage;
        public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult) => inner.GetLocations(fileSystem, gameLocatorResult);
        public GamePath GetPrimaryFile(GameInstallation installation) => inner.GetPrimaryFile(installation);
        public string ThunderstoreCommunity => thunderstore.ThunderstoreCommunity;
        public string ThunderstoreLoaderPackage => thunderstore.ThunderstoreLoaderPackage;
        public IReadOnlyList<string> ThunderstoreLoaderAliases => thunderstore.ThunderstoreLoaderAliases;
    }

    private async Task<(LoadoutId Id, IGameData Game)> SeedLoadout()
    {
        var row = await SeedLoadoutRow(GameStore.Steam, "Thunderstore loadout");
        var installation = Provider.GetRequiredService<IGameRegistry>().LocateGameInstallations().First();
        var stub = new ThunderstoreStubbedGame(Provider);
        return (row.LoadoutId, new ThunderstoreGameOfLoadout((IGameData)installation.Game, stub));
    }

    private async Task<(ThunderstoreViewModel Vm, FakeCatalogSource Source)> Open(FakeCatalogSource source, FakeInstallSource? installs = null, ILoadoutManager? loadoutManager = null, IStoreSortMemory? sortMemory = null)
    {
        installs ??= new FakeInstallSource();
        loadoutManager ??= Substitute.For<ILoadoutManager>();
        var (loadoutId, game) = await SeedLoadout();
        var vm = new ThunderstoreViewModel(
            Provider.GetRequiredService<IWindowManager>(),
            Connection,
            [game],
            source,
            installs,
            loadoutManager,
            Substitute.For<IOSInterop>(),
            Substitute.For<IThumbnailLoader>(),
            sortMemory ?? new InMemoryStoreSortMemory(),
            NullLogger<ThunderstoreViewModel>.Instance)
        {
            LoadoutId = loadoutId,
        };
        await vm.LoadAsync(CancellationToken.None);
        return (vm, source);
    }

    private static string[] Names(ThunderstoreViewModel vm) => vm.Packages.Select(p => p.FullName).ToArray();

    [Fact]
    public async Task Open_ListsTheCommunityCatalogBestRatedFirstWithoutDeprecatedPackages()
    {
        var (vm, source) = await Open(new FakeCatalogSource(Catalog()));

        source.Communities.Should().Equal("repo");
        Names(vm).Should().Equal("BepInEx-BepInExPack", "Zehs-REPOLib", "nickklmao-REPOConfig", "Packs-BigPack");
        vm.ErrorText.Should().BeEmpty();
        vm.Packages[1].LatestVersion.Should().Be("4.2.0");
        vm.Packages[1].Description.Should().Be("REPOLib does things");
        vm.Packages[1].Rating.Should().Be(900);
        vm.Packages[1].PackageUrl.Should().Be(new Uri("https://thunderstore.io/c/repo/p/Zehs/REPOLib/"));
    }

    [Fact]
    public async Task SearchText_FiltersTheList()
    {
        var (vm, _) = await Open(new FakeCatalogSource(Catalog()));

        vm.SearchText = "repolib";

        Names(vm).Should().Equal("Zehs-REPOLib");
    }

    private static ThunderstoreCatalog CatalogWithModManagers() => new("repo",
    [
        Package("Zehs", "REPOLib", rating: 900),
        Package("ebkr", "r2modman", rating: 5000),
        Package("Kesomannen", "GaleModManager", rating: 4000),
        Package("someone", "r2modman_Profile_Sync", rating: 20),
        Package("OldManager", "Abandoned", rating: 30, deprecated: true),
    ]);

    [Fact]
    public async Task OtherModManagers_AreNotListed()
    {
        var (vm, _) = await Open(new FakeCatalogSource(CatalogWithModManagers()));

        Names(vm).Should().Equal("Zehs-REPOLib", "someone-r2modman_Profile_Sync");
    }

    [Fact]
    public async Task OtherModManagers_StayHiddenWithDeprecatedShown()
    {
        var (vm, _) = await Open(new FakeCatalogSource(CatalogWithModManagers()));

        vm.ShowDeprecated = true;

        Names(vm).Should().NotContain(["ebkr-r2modman", "Kesomannen-GaleModManager"]);
        Names(vm).Should().Contain("someone-r2modman_Profile_Sync");
    }

    [Fact]
    public async Task SearchingForAModManager_FindsNoManager()
    {
        var (vm, _) = await Open(new FakeCatalogSource(CatalogWithModManagers()));

        vm.SearchText = "r2modman";
        Names(vm).Should().Equal("someone-r2modman_Profile_Sync");

        vm.SearchText = "gale";
        Names(vm).Should().BeEmpty();
    }

    [Fact]
    public async Task ModManagerNames_MatchRegardlessOfCase()
    {
        var catalog = new ThunderstoreCatalog("repo", [Package("EBKR", "R2ModMan", rating: 1)]);
        var (vm, _) = await Open(new FakeCatalogSource(catalog));

        Names(vm).Should().BeEmpty();
    }

    [Fact]
    public async Task ShowDeprecated_AddsDeprecatedPackagesAndFlagsThem()
    {
        var (vm, _) = await Open(new FakeCatalogSource(Catalog()));

        vm.ShowDeprecated = true;

        Names(vm).Should().Equal("Old-Abandoned", "BepInEx-BepInExPack", "Zehs-REPOLib", "nickklmao-REPOConfig", "Packs-BigPack");
        vm.Packages[0].IsDeprecated.Should().BeTrue();
        vm.Packages[1].IsDeprecated.Should().BeFalse();
    }

    [Fact]
    public async Task StaleCache_IsRefreshedOnceOnOpen()
    {
        var (_, source) = await Open(new FakeCatalogSource(Catalog()) { Stale = true });

        source.Refreshes.Should().Be(1);
    }

    [Fact]
    public async Task FreshCache_IsNotRefreshedOnOpen()
    {
        var (_, source) = await Open(new FakeCatalogSource(Catalog()) { Stale = false });

        source.Refreshes.Should().Be(0);
    }

    [Fact]
    public async Task RefreshCommand_RefreshesEvenWhenTheCacheIsFresh()
    {
        var (vm, source) = await Open(new FakeCatalogSource(Catalog()) { Stale = false });

        await vm.RefreshCommand.Execute();

        source.Refreshes.Should().Be(1);
        Names(vm).Should().HaveCount(4);
    }

    [Fact]
    public async Task UnavailableIndex_BecomesTheErrorTextAndTheListIsEmpty()
    {
        var failure = new ThunderstoreIndexUnavailableException("repo");
        var (vm, _) = await Open(new FakeCatalogSource(null) { LoadFailure = failure });

        vm.ErrorText.Should().Be(failure.Message);
        vm.Packages.Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshFailure_ShowsTheErrorButKeepsTheCachedList()
    {
        var (vm, _) = await Open(new FakeCatalogSource(Catalog()) { Stale = true, RefreshFailure = new HttpRequestException("connection refused") });

        vm.ErrorText.Should().Contain("connection refused");
        Names(vm).Should().HaveCount(4);
    }

    [Fact]
    public async Task AnUnreadableCachedIndex_BecomesTheErrorTextOnOpen()
    {
        var (vm, _) = await Open(new FakeCatalogSource(Catalog()) { StaleFailure = new InvalidDataException("index.json is corrupt") });

        vm.ErrorText.Should().Contain("index.json is corrupt");
    }

    [Fact]
    public async Task ASuccessfulRefresh_ClearsTheErrorText()
    {
        var source = new FakeCatalogSource(Catalog()) { Stale = true, RefreshFailure = new HttpRequestException("connection refused") };
        var (vm, _) = await Open(source);
        vm.ErrorText.Should().NotBeEmpty();

        source.RefreshFailure = null;
        await vm.RefreshCommand.Execute();

        vm.ErrorText.Should().BeEmpty();
    }

    [Fact]
    public async Task AGameWithoutAThunderstoreCommunity_HasNoCatalog()
    {
        var vm = new ThunderstoreViewModel(
            Provider.GetRequiredService<IWindowManager>(), Connection, [], new FakeCatalogSource(Catalog()),
            new FakeInstallSource(), Substitute.For<ILoadoutManager>(), Substitute.For<IOSInterop>(), Substitute.For<IThumbnailLoader>(), new InMemoryStoreSortMemory(), NullLogger<ThunderstoreViewModel>.Instance)
        {
            LoadoutId = (await SeedLoadout()).Id,
        };

        await vm.LoadAsync(CancellationToken.None);

        vm.Packages.Should().BeEmpty();
        vm.ErrorText.Should().NotBeEmpty();
    }

    private static ThunderstoreVersionRef Ref(string team, string name, string version)
        => new(new ThunderstorePackageRef(team, name), ThunderstoreVersionNumber.TryParse(version, out var number) ? number : throw new ArgumentException(version));

    [Fact]
    public async Task InstallCommand_AsksTheSourceForTheLatestVersionAndInstallsEachNewFileInOrder()
    {
        var loader = await AddLibraryFile("BepInEx", "BepInExPack", "5.4.2305");
        var alreadyThere = await AddLibraryFile("Zehs", "REPOLib", "4.1.0");
        var mod = await AddLibraryFile("nickklmao", "REPOConfig", "1.0.0");
        var installs = new FakeInstallSource { Files = [loader, alreadyThere, mod] };
        var manager = LoadoutManagerThatInstalls();
        var (vm, _) = await Open(new FakeCatalogSource(Catalog()), installs, manager);
        await LinkIntoLoadout(vm.LoadoutId, alreadyThere);

        await vm.InstallCommand.Execute(vm.Packages.Single(p => p.FullName == "nickklmao-REPOConfig"));

        var request = installs.Requests.Should().ContainSingle().Subject;
        request.Root.Should().Be(Ref("nickklmao", "REPOConfig", "1.0.0"));
        request.WithDependencies.Should().BeTrue();
        request.Policy.Should().Be(DependencyPolicy.Latest);
        request.InstalledInLoadout.Should().Equal(new Dictionary<ThunderstorePackageRef, ThunderstoreVersionNumber>
        {
            [new ThunderstorePackageRef("Zehs", "REPOLib")] = Ref("Zehs", "REPOLib", "4.1.0").Version,
        });
        manager.ReceivedCalls().Select(call => ((LibraryItem.ReadOnly)call.GetArguments()[0]!).Id)
            .Should().Equal(loader.Id, mod.Id);
        vm.ErrorText.Should().BeEmpty();
    }

    [Fact]
    public async Task InstallCommand_TakesTheExactDependencyVersionsOfAModpack()
    {
        var installs = new FakeInstallSource();
        var (vm, _) = await Open(new FakeCatalogSource(Catalog()), installs, LoadoutManagerThatInstalls());

        await vm.InstallCommand.Execute(vm.Packages.Single(p => p.FullName == "Packs-BigPack"));

        installs.Requests.Should().ContainSingle().Which.Policy.Should().Be(DependencyPolicy.Exact);
    }

    [Fact]
    public async Task InstallCommand_ShowsASourceFailureAndKeepsTheList()
    {
        var installs = new FakeInstallSource { Failure = new DependencyResolutionException("Zehs-REPOLib needs Missing-Thing, which is not in the index.") };
        var manager = LoadoutManagerThatInstalls();
        var (vm, _) = await Open(new FakeCatalogSource(Catalog()), installs, manager);
        var before = Names(vm);

        await vm.InstallCommand.Execute(vm.Packages[0]);

        vm.ErrorText.Should().Contain("Missing-Thing");
        Names(vm).Should().Equal(before);
        manager.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task OpenPackageCommand_OpensThePackagePage()
    {
        var os = Substitute.For<IOSInterop>();
        var (loadoutId, game) = await SeedLoadout();
        var withOs = new ThunderstoreViewModel(Provider.GetRequiredService<IWindowManager>(), Connection,
            [game], new FakeCatalogSource(Catalog()), new FakeInstallSource(), Substitute.For<ILoadoutManager>(), os, Substitute.For<IThumbnailLoader>(), new InMemoryStoreSortMemory(), NullLogger<ThunderstoreViewModel>.Instance)
        {
            LoadoutId = loadoutId,
        };
        await withOs.LoadAsync(CancellationToken.None);

        await withOs.OpenPackageCommand.Execute(withOs.Packages[0]);

        os.Received(1).OpenUri(new Uri("https://thunderstore.io/c/repo/p/BepInEx/BepInExPack/"));
    }

    [Fact]
    public async Task Rows_CarryOwnerReadableNameIconAndDownloads()
    {
        var catalog = new ThunderstoreCatalog("repo",
        [
            Package("glarmer", "PEAK_Unlimited", rating: 112, icon: "https://gcdn.thunderstore.io/live/repository/icons/glarmer-PEAK_Unlimited-4.0.2.png", downloads: 1_234_567),
        ]);
        var (vm, _) = await Open(new FakeCatalogSource(catalog));

        var row = vm.Packages.Should().ContainSingle().Subject;
        row.Owner.Should().Be("glarmer");
        row.DisplayName.Should().Be("PEAK Unlimited");
        row.FullName.Should().Be("glarmer-PEAK_Unlimited");
        row.IconUrl.Should().Be(new Uri("https://gcdn.thunderstore.io/live/repository/icons/glarmer-PEAK_Unlimited-4.0.2.png"));
        row.Downloads.Should().Be(1_234_567);
    }

    [Fact]
    public async Task Rows_DownloadsAreSummedOverAllVersions()
    {
        var package = Package("Zehs", "REPOLib", rating: 1) with
        {
            Versions =
            [
                new PackageVersion { Name = "REPOLib", FullName = "Zehs-REPOLib-2.0.0", VersionNumber = "2.0.0", DownloadUrl = "https://example.com/2", Icon = "https://example.com/new.png", Downloads = 40 },
                new PackageVersion { Name = "REPOLib", FullName = "Zehs-REPOLib-1.0.0", VersionNumber = "1.0.0", DownloadUrl = "https://example.com/1", Icon = "https://example.com/old.png", Downloads = 2 },
            ],
        };
        var (vm, _) = await Open(new FakeCatalogSource(new ThunderstoreCatalog("repo", [package])));

        vm.Packages[0].Downloads.Should().Be(42);
        vm.Packages[0].IconUrl.Should().Be(new Uri("https://example.com/new.png"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    public async Task Rows_WithoutAUsableIcon_HaveNoIconUrl(string? icon)
    {
        var (vm, _) = await Open(new FakeCatalogSource(new ThunderstoreCatalog("repo", [Package("Zehs", "REPOLib", rating: 1, icon: icon)])));

        vm.Packages[0].IconUrl.Should().BeNull();
    }

    [Fact]
    public async Task Search_StillMatchesTheOwnerAndTheRawFullName()
    {
        var catalog = new ThunderstoreCatalog("repo", [Package("glarmer", "PEAK_Unlimited", rating: 1), Package("Zehs", "REPOLib", rating: 2)]);
        var (vm, _) = await Open(new FakeCatalogSource(catalog));

        vm.SearchText = "glarmer";
        Names(vm).Should().Equal("glarmer-PEAK_Unlimited");

        vm.SearchText = "PEAK_Unlimited";
        Names(vm).Should().Equal("glarmer-PEAK_Unlimited");
    }

    private static readonly DateTimeOffset Jan = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static ThunderstoreCatalog SortableCatalog() => new("repo",
    [
        Package("a", "Alpha", rating: 10, downloads: 500, created: Jan.AddDays(30), updated: Jan.AddDays(5)),
        Package("b", "Bravo", rating: 90, downloads: 20, created: Jan.AddDays(10), updated: Jan.AddDays(40)),
        Package("c", "Charlie", rating: 50, downloads: 3000, created: Jan.AddDays(20), updated: Jan.AddDays(20)),
        Package("d", "Pinned_Delta", rating: 1, downloads: 1, created: Jan, updated: Jan, pinned: true),
    ]);

    [Fact]
    public async Task TopRated_IsTheDefaultAndListsPinnedPackagesFirst()
    {
        var (vm, _) = await Open(new FakeCatalogSource(SortableCatalog()));

        vm.Sort.Should().Be(ThunderstoreSort.TopRated);
        Names(vm).Should().Equal("d-Pinned_Delta", "b-Bravo", "c-Charlie", "a-Alpha");
    }

    [Theory]
    [InlineData(ThunderstoreSort.Downloads, new[] { "c-Charlie", "a-Alpha", "b-Bravo", "d-Pinned_Delta" })]
    [InlineData(ThunderstoreSort.Updated, new[] { "b-Bravo", "c-Charlie", "a-Alpha", "d-Pinned_Delta" })]
    [InlineData(ThunderstoreSort.Newest, new[] { "a-Alpha", "c-Charlie", "b-Bravo", "d-Pinned_Delta" })]
    [InlineData(ThunderstoreSort.Name, new[] { "a-Alpha", "b-Bravo", "c-Charlie", "d-Pinned_Delta" })]
    public async Task EachSort_OrdersThePackages_AndLeavesPinnedPackagesWhereTheirValueFalls(ThunderstoreSort sort, string[] expected)
    {
        var (vm, _) = await Open(new FakeCatalogSource(SortableCatalog()));

        vm.Sort = sort;

        Names(vm).Should().Equal(expected);
    }

    [Fact]
    public async Task NameSort_IgnoresCaseAndBreaksTiesByFullName()
    {
        var catalog = new ThunderstoreCatalog("repo",
        [
            Package("z", "same", rating: 1),
            Package("y", "Same", rating: 1),
            Package("x", "apple", rating: 1),
            Package("w", "Banana", rating: 1),
        ]);
        var (vm, _) = await Open(new FakeCatalogSource(catalog));

        vm.Sort = ThunderstoreSort.Name;

        Names(vm).Should().Equal("x-apple", "w-Banana", "y-Same", "z-same");
    }

    [Fact]
    public async Task Sorting_KeepsTheSearchFilter()
    {
        var (vm, _) = await Open(new FakeCatalogSource(SortableCatalog()));
        vm.SearchText = "a";

        vm.Sort = ThunderstoreSort.Downloads;

        Names(vm).Should().Equal("c-Charlie", "a-Alpha", "b-Bravo", "d-Pinned_Delta");
        vm.SearchText = "bravo";
        Names(vm).Should().Equal("b-Bravo");
    }

    [Fact]
    public async Task APackageWithoutDates_SortsLastByDate()
    {
        var catalog = new ThunderstoreCatalog("repo", [Package("a", "Undated", rating: 1), Package("b", "Dated", rating: 1, updated: Jan)]);
        var (vm, _) = await Open(new FakeCatalogSource(catalog));

        vm.Sort = ThunderstoreSort.Updated;

        Names(vm).Should().Equal("b-Dated", "a-Undated");
    }

    [Fact]
    public async Task TheChosenSort_IsRestoredWhenThePageIsOpenedAgain()
    {
        var memory = new InMemoryStoreSortMemory();
        var (first, _) = await Open(new FakeCatalogSource(SortableCatalog()), sortMemory: memory);
        first.Sort = ThunderstoreSort.Name;

        var (second, _) = await Open(new FakeCatalogSource(SortableCatalog()), sortMemory: memory);

        second.Sort.Should().Be(ThunderstoreSort.Name);
        Names(second).Should().Equal("a-Alpha", "b-Bravo", "c-Charlie", "d-Pinned_Delta");
    }

    [Fact]
    public async Task TheSortIsRememberedPerCommunity()
    {
        var memory = new InMemoryStoreSortMemory();
        memory.Remember("thunderstore", "some-other-community", nameof(ThunderstoreSort.Name));

        var (vm, _) = await Open(new FakeCatalogSource(SortableCatalog()), sortMemory: memory);

        vm.Sort.Should().Be(ThunderstoreSort.TopRated);
    }

    [Fact]
    public async Task ASavedKeyNoSortHas_FallsBackToTopRated()
    {
        var memory = new InMemoryStoreSortMemory();
        memory.Remember("thunderstore", "repo", "Endorsements");

        var (vm, _) = await Open(new FakeCatalogSource(SortableCatalog()), sortMemory: memory);

        vm.Sort.Should().Be(ThunderstoreSort.TopRated);
    }

    [Fact]
    public void TheSortChoices_UseTheSharedWording()
    {
        ThunderstoreSorting.Choices.Select(c => c.Label).Should().Equal("Top rated", "Most downloaded", "Recently updated", "Newest", "Name");
        ThunderstoreSorting.Choices.Select(c => c.Key).Should().Equal(Enum.GetNames<ThunderstoreSort>());
    }
}
