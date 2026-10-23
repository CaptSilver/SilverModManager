using System.Reactive.Linq;
using System.Collections.Immutable;
using System.Net.Http;
using DynamicData.Kernel;
using NexusMods.Abstractions.Games;
using NexusMods.Paths;
using NexusMods.Sdk.IO;
using NexusMods.Sdk.NexusModsApi;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Abstractions.Loadouts;
using NexusMods.App.UI.Pages.Thunderstore;
using NexusMods.App.UI.Windows;
using NexusMods.Networking.Thunderstore;
using NexusMods.Networking.Thunderstore.DTOs;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk;
using System.Security.Cryptography;
using NexusMods.Abstractions.Library;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Hashing.xxHash3;
using NexusMods.Sdk.Hashes;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Thunderstore;
using NexusMods.StandardGameLocators.TestHelpers.StubbedGames;
using NSubstitute;
using Loadout = NexusMods.Sdk.Loadouts.Loadout;

namespace NexusMods.UI.Tests.Pages;

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

    private static PackageListing Package(string team, string name, double rating, bool deprecated = false, string version = "1.0.0", string[]? categories = null)
        => new()
        {
            Name = name,
            FullName = $"{team}-{name}",
            Owner = team,
            PackageUrl = $"https://thunderstore.io/c/repo/p/{team}/{name}/",
            RatingScore = rating,
            IsDeprecated = deprecated,
            Categories = categories ?? [],
            Versions =
            [
                new PackageVersion
                {
                    Name = name,
                    FullName = $"{team}-{name}-{version}",
                    VersionNumber = version,
                    Description = $"{name} does things",
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

    private async Task<(ThunderstoreViewModel Vm, FakeCatalogSource Source)> Open(FakeCatalogSource source, FakeInstallSource? installs = null, ILoadoutManager? loadoutManager = null)
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
            new FakeInstallSource(), Substitute.For<ILoadoutManager>(), Substitute.For<IOSInterop>(), NullLogger<ThunderstoreViewModel>.Instance)
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
            [game], new FakeCatalogSource(Catalog()), new FakeInstallSource(), Substitute.For<ILoadoutManager>(), os, NullLogger<ThunderstoreViewModel>.Instance)
        {
            LoadoutId = loadoutId,
        };
        await withOs.LoadAsync(CancellationToken.None);

        await withOs.OpenPackageCommand.Execute(withOs.Packages[0]);

        os.Received(1).OpenUri(new Uri("https://thunderstore.io/c/repo/p/BepInEx/BepInExPack/"));
    }
}
