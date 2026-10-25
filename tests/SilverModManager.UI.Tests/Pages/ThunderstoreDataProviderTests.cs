using System.Collections.Concurrent;
using System.Collections.Immutable;
using SilverModManager.App.UI;
using SilverModManager.App.UI.Pages.Thunderstore;
using Avalonia.Media.Imaging;
using SilverModManager.Sdk.Resources;
using System.Security.Cryptography;
using DynamicData;
using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Pages;
using SilverModManager.App.UI.Pages.LibraryPage;
using SilverModManager.App.UI.Pages.LoadoutPage;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Networking.Thunderstore;
using SilverModManager.Networking.Thunderstore.DTOs;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// The library page lists Thunderstore packages once per package, with the downloaded versions underneath.
/// Every test uses a community of its own, so rows seeded by one test never show up in another.
/// </summary>
public class ThunderstoreDataProviderTests(IServiceProvider provider) : AUiTest(provider)
{
    private static readonly LoadoutId NoLoadout = LoadoutId.From(EntityId.From(0xBEEF));

    private static string UniqueCommunity() => "community-" + Guid.NewGuid().ToString("N");

    private static IGameData ThunderstoreGameOf(string community) => new CommunityGame(community);

    private static IGameData PlainGame() => new PlainTestGame();

    private class PlainTestGame : IGameData
    {
        public GameId GameId { get; } = GameId.From(Guid.NewGuid().ToString("N"));
        public StoreIdentifiers StoreIdentifiers => new(GameId);
        public string DisplayName => "Test game";
        public Optional<NexusModsGameId> NexusModsGameId => Optional<NexusModsGameId>.None;
        public IStreamFactory IconImage => throw new NotSupportedException();
        public IStreamFactory TileImage => throw new NotSupportedException();
        public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult) => throw new NotSupportedException();
        public GamePath GetPrimaryFile(GameInstallation installation) => throw new NotSupportedException();
    }

    private sealed class CommunityGame(string community) : PlainTestGame, IThunderstoreGame
    {
        public string ThunderstoreCommunity => community;
        public string ThunderstoreLoaderPackage => "BepInEx-BepInExPack";
        public IReadOnlyList<string> ThunderstoreLoaderAliases => [];
    }

    private ThunderstoreDataProvider CreateProvider(params IGameData[] games) => CreateProvider(new FakeIconPipeline(), games);

    private ThunderstoreDataProvider CreateProvider(FakeIconPipeline iconPipeline, params IGameData[] games) =>
        CreateProvider(new ThunderstoreIconLoader(iconPipeline, ImagePipelines.ModPageThumbnailFallback), games);

    private ThunderstoreDataProvider CreateProvider(IThunderstoreIconLoader iconLoader, params IGameData[] games)
    {
        var services = new ServiceCollection()
            .AddSingleton(Connection)
            .AddSingleton(Provider.GetRequiredService<ThunderstoreUpdateChecker>());
        foreach (var game in games) services.AddSingleton(game);
        return new ThunderstoreDataProvider(services.BuildServiceProvider(), iconLoader);
    }

    private sealed class FakeIconPipeline : IResourceLoader<Uri, Bitmap>
    {
        public ConcurrentQueue<Uri> Requests { get; } = new();
        public Func<Uri, Bitmap> Respond { get; set; } = _ => throw new InvalidOperationException("no response set");

        public ValueTask<Resource<Bitmap>> LoadResourceAsync(Uri resourceIdentifier, CancellationToken cancellationToken)
        {
            Requests.Enqueue(resourceIdentifier);
            return new ValueTask<Resource<Bitmap>>(new Resource<Bitmap> { Data = Respond(resourceIdentifier) });
        }
    }

    private static Bitmap NewBitmap() => new RenderTargetBitmap(new Avalonia.PixelSize(4, 4));

    private async Task<EntityId> AddFile(string community, string team, string name, string version, bool frozen = false, string? itemName = null)
    {
        var fileName = $"{team}-{name}-{version}.zip";
        var bytes = System.Text.Encoding.UTF8.GetBytes(fileName);
        using var tx = Connection.BeginTransaction();
        var file = new LibraryFile.New(tx, out var id)
        {
            FileName = fileName,
            Hash = bytes.xxHash3(),
            Size = Size.From((ulong)bytes.Length),
            LibraryItem = new LibraryItem.New(tx, id) { Name = itemName ?? fileName },
        };
        _ = new ThunderstoreLibraryFile.New(tx, id)
        {
            LibraryFile = file,
            Namespace = team,
            Name = name,
            VersionNumber = version,
            Community = community,
            PinnedSha256 = Sha256Value.From(SHA256.HashData(bytes)),
        };
        if (frozen) _ = new UpdatePin.New(tx, id) { Frozen = true };
        var result = await tx.Commit();
        return result[id];
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

    private static IObservableCache<CompositeItemModel<EntityId>, EntityId> Rows(IObservable<IChangeSet<CompositeItemModel<EntityId>, EntityId>> changes)
        => changes.AsObservableCache();

    private static IObservableCache<CompositeItemModel<EntityId>, EntityId> ChildrenOf(CompositeItemModel<EntityId> parent)
        => parent.ChildrenObservable.AsObservableCache();

    private static string NameOf(CompositeItemModel<EntityId> row) => row.Get<NameComponent>(SharedColumns.Name.NameComponentKey).Value.Value;

    [Fact]
    public async Task LibraryItems_AreOneParentPerPackageWithItsVersionsUnderneath()
    {
        var community = UniqueCommunity();
        var team = "Zehs";
        await AddFile(community, team, "REPOLib", "4.1.0");
        await AddFile(community, team, "REPOLib", "4.2.0");
        await AddFile(community, "nickklmao", "REPOConfig", "1.2.6");
        var game = ThunderstoreGameOf(community);
        var filter = new LibraryFilter(NoLoadout, game);
        var thunderstore = CreateProvider(game);

        using var rows = Rows(thunderstore.ObserveLibraryItems(filter));

        await Eventually(() => rows.Count.Should().Be(2));
        rows.Items.Select(NameOf).Should().BeEquivalentTo("REPOLib", "REPOConfig");

        var repoLib = rows.Items.Single(row => NameOf(row) == "REPOLib");
        using var versions = ChildrenOf(repoLib);
        await Eventually(() => versions.Count.Should().Be(2));
        versions.Items.Select(row => row.Get<VersionComponent>(LibraryColumns.ItemVersion.CurrentVersionComponentKey).Value.Value)
            .Should().BeEquivalentTo("4.1.0", "4.2.0");
    }

    [Fact]
    public async Task ParentShowsTheHighestVersionByNumber()
    {
        var community = UniqueCommunity();
        await AddFile(community, "Team", "Mod", "4.9.0");
        await AddFile(community, "Team", "Mod", "4.10.0");
        var game = ThunderstoreGameOf(community);

        using var rows = Rows(CreateProvider(game).ObserveLibraryItems(new LibraryFilter(NoLoadout, game)));

        await Eventually(() => rows.Count.Should().Be(1));
        var parent = rows.Items.Single();
        await OnUi(() =>
        {
            parent.Activate();
            return Task.CompletedTask;
        });
        await EventuallyOnUi(() =>
        {
            parent.Get<VersionComponent>(LibraryColumns.ItemVersion.CurrentVersionComponentKey).Value.Value.Should().Be("4.10.0");
        });
    }

    [Fact]
    public async Task ARowShowsTheReadablePackageName_AndWhoPublishedIt()
    {
        var community = UniqueCommunity();
        await AddFile(community, "Zehs", "Unlimited_Orbs", "1.0.0");
        var game = ThunderstoreGameOf(community);
        using var rows = Rows(CreateProvider(game).ObserveLibraryItems(new LibraryFilter(NoLoadout, game)));
        await Eventually(() => rows.Count.Should().Be(1));

        var parent = rows.Items.Single();
        NameOf(parent).Should().Be("Unlimited Orbs");
        parent.Get<StringComponent>(SharedColumns.Name.SubtitleComponentKey).Value.Value.Should().Be("by Zehs");
        using var versions = ChildrenOf(parent);
        await Eventually(() => versions.Count.Should().Be(1));
        NameOf(versions.Items.Single()).Should().Be("Unlimited Orbs");
    }

    [Fact]
    public async Task ARenamedLibraryItem_KeepsTheUsersName()
    {
        var community = UniqueCommunity();
        await AddFile(community, "Zehs", "Unlimited_Orbs", "1.0.0", itemName: "My orbs");
        var game = ThunderstoreGameOf(community);
        using var rows = Rows(CreateProvider(game).ObserveLibraryItems(new LibraryFilter(NoLoadout, game)));
        await Eventually(() => rows.Count.Should().Be(1));

        using var versions = ChildrenOf(rows.Items.Single());
        await Eventually(() => versions.Count.Should().Be(1));
        NameOf(versions.Items.Single()).Should().Be("My orbs");
    }

    [Fact]
    public async Task ARowAsksForTheIconOfItsNewestVersion_OnceShown()
    {
        var community = UniqueCommunity();
        await AddFile(community, "Zehs", "REPOLib", "4.1.0");
        await AddFile(community, "Zehs", "REPOLib", "4.10.0");
        await AddFile(community, "Zehs", "REPOLib", "4.2.0");
        var game = ThunderstoreGameOf(community);
        var loaded = NewBitmap();
        var pipeline = new FakeIconPipeline { Respond = _ => loaded };
        using var rows = Rows(CreateProvider(pipeline, game).ObserveLibraryItems(new LibraryFilter(NoLoadout, game)));
        await Eventually(() => rows.Count.Should().Be(1));
        var parent = rows.Items.Single();
        var icon = parent.Get<ImageComponent>(SharedColumns.Name.ImageComponentKey);

        pipeline.Requests.Should().BeEmpty("nothing is fetched before the row is shown");
        icon.Value.Value.Should().BeSameAs(ImagePipelines.ModPageThumbnailFallback);

        await OnUi(() => parent.Activate());

        await EventuallyOnUi(() => icon.Value.Value.Should().BeSameAs(loaded));
        pipeline.Requests.Should().Equal(new Uri("https://gcdn.thunderstore.io/live/repository/icons/Zehs-REPOLib-4.10.0.png"));
    }

    [Fact]
    public async Task AnIconThatFailsToLoad_ShowsThePlaceholder()
    {
        var community = UniqueCommunity();
        await AddFile(community, "Zehs", "REPOLib", "4.1.0");
        var game = ThunderstoreGameOf(community);
        var pipeline = new FakeIconPipeline { Respond = _ => throw new HttpRequestException("offline") };
        // A placeholder of its own, so the row is seen to switch to it rather than just keep its first image.
        var placeholder = NewBitmap();
        var loader = new ThunderstoreIconLoader(pipeline, placeholder);
        using var rows = Rows(CreateProvider(loader, game).ObserveLibraryItems(new LibraryFilter(NoLoadout, game)));
        await Eventually(() => rows.Count.Should().Be(1));
        var parent = rows.Items.Single();
        var icon = parent.Get<ImageComponent>(SharedColumns.Name.ImageComponentKey);

        await OnUi(() => parent.Activate());

        await EventuallyOnUi(() => icon.Value.Value.Should().BeSameAs(placeholder));
        pipeline.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task ARowShownAgain_TakesItsIconFromTheCache()
    {
        var community = UniqueCommunity();
        await AddFile(community, "Zehs", "REPOLib", "4.1.0");
        var game = ThunderstoreGameOf(community);
        var loaded = NewBitmap();
        var pipeline = new FakeIconPipeline { Respond = _ => loaded };
        var thunderstore = CreateProvider(pipeline, game);

        using (var first = Rows(thunderstore.ObserveLibraryItems(new LibraryFilter(NoLoadout, game))))
        {
            await Eventually(() => first.Count.Should().Be(1));
            var icon = first.Items.Single().Get<ImageComponent>(SharedColumns.Name.ImageComponentKey);
            await OnUi(() => first.Items.Single().Activate());
            await EventuallyOnUi(() => icon.Value.Value.Should().BeSameAs(loaded));
        }

        using var again = Rows(thunderstore.ObserveLibraryItems(new LibraryFilter(NoLoadout, game)));
        await Eventually(() => again.Count.Should().Be(1));
        again.Items.Single().Get<ImageComponent>(SharedColumns.Name.ImageComponentKey).Value.Value.Should().BeSameAs(loaded);
        pipeline.Requests.Should().HaveCount(1);
    }

    private static string SourceOf(CompositeItemModel<EntityId> row) => row.Get<StringComponent>(SharedColumns.Source.ComponentKey).Value.Value;

    [Fact]
    public async Task LibraryRows_AreFromThunderstore_ParentAndVersions()
    {
        var community = UniqueCommunity();
        await AddFile(community, "Zehs", "REPOLib", "4.1.0");
        var game = ThunderstoreGameOf(community);
        using var rows = Rows(CreateProvider(game).ObserveLibraryItems(new LibraryFilter(NoLoadout, game)));
        await Eventually(() => rows.Count.Should().Be(1));

        SourceOf(rows.Items.Single()).Should().Be("Thunderstore");
        using var versions = ChildrenOf(rows.Items.Single());
        await Eventually(() => versions.Count.Should().Be(1));
        SourceOf(versions.Items.Single()).Should().Be("Thunderstore");
    }

    [Fact]
    public async Task LoadoutRows_AreFromThunderstore_ParentAndItems()
    {
        var community = UniqueCommunity();
        var game = ThunderstoreGameOf(community);
        var loadout = await AddLoadout(game);
        await Install(loadout, await AddFile(community, "Zehs", "REPOLib", "4.1.0"), "REPOLib");
        using var rows = Rows(CreateProvider(game).ObserveLoadoutItems(WholeLoadout(loadout.Loadout)));
        await Eventually(() => rows.Count.Should().Be(1));

        SourceOf(rows.Items.Single()).Should().Be("Thunderstore");
        using var items = ChildrenOf(rows.Items.Single());
        await Eventually(() => items.Count.Should().Be(1));
        SourceOf(items.Items.Single()).Should().Be("Thunderstore");
    }

    [Fact]
    public async Task CountLibraryItems_IsThePackageCount()
    {
        var community = UniqueCommunity();
        await AddFile(community, "Zehs", "REPOLib", "4.1.0");
        await AddFile(community, "Zehs", "REPOLib", "4.2.0");
        await AddFile(community, "nickklmao", "REPOConfig", "1.2.6");
        var game = ThunderstoreGameOf(community);
        var count = 0;

        using var _ = CreateProvider(game).CountLibraryItems(new LibraryFilter(NoLoadout, game)).Subscribe(value => count = value);

        await Eventually(() => count.Should().Be(2));
    }

    [Fact]
    public async Task ItemsOfAnotherCommunity_AreNotShown()
    {
        var community = UniqueCommunity();
        var other = UniqueCommunity();
        await AddFile(community, "Zehs", "REPOLib", "4.1.0");
        await AddFile(other, "Someone", "ElsewhereMod", "1.0.0");
        var game = ThunderstoreGameOf(community);
        var thunderstore = CreateProvider(game);
        var filter = new LibraryFilter(NoLoadout, game);
        var count = -1;

        using var rows = Rows(thunderstore.ObserveLibraryItems(filter));
        using var _ = thunderstore.CountLibraryItems(filter).Subscribe(value => count = value);

        await Eventually(() => rows.Count.Should().Be(1));
        NameOf(rows.Items.Single()).Should().Be("REPOLib");
        await Eventually(() => count.Should().Be(1));
    }

    [Fact]
    public async Task AGameWithoutThunderstoreSupport_ShowsNothingAndCountsZero()
    {
        await AddFile(UniqueCommunity(), "Zehs", "REPOLib", "4.1.0");
        var game = PlainGame();
        var thunderstore = CreateProvider(game);
        var filter = new LibraryFilter(NoLoadout, game);
        var count = -1;

        using var rows = Rows(thunderstore.ObserveLibraryItems(filter));
        using var _ = thunderstore.CountLibraryItems(filter).Subscribe(value => count = value);

        await Eventually(() => count.Should().Be(0));
        rows.Count.Should().Be(0);
    }

    [Fact]
    public async Task GetAllFiles_ReturnsTheFilesOfTheGamesCommunityOnly()
    {
        var community = UniqueCommunity();
        var mine = await AddFile(community, "Zehs", "REPOLib", "4.1.0");
        await AddFile(UniqueCommunity(), "Someone", "ElsewhereMod", "1.0.0");
        var game = ThunderstoreGameOf(community);
        var plain = PlainGame();
        var thunderstore = CreateProvider(game, plain);

        thunderstore.GetAllFiles(game.GameId).Select(file => file.Id).Should().Equal(mine);
        thunderstore.GetAllFiles(plain.GameId).Should().BeEmpty();
    }

    [Fact]
    public async Task ARowWithAnUpdate_ShowsTheNewVersion_AFrozenOneDoesNot()
    {
        var community = UniqueCommunity();
        var team = "Team" + Guid.NewGuid().ToString("N");
        await AddFile(community, team, "Updatable", "1.0.0");
        await AddFile(community, team, "Frozen", "1.0.0", frozen: true);
        var game = ThunderstoreGameOf(community);
        using var rows = Rows(CreateProvider(game).ObserveLibraryItems(new LibraryFilter(NoLoadout, game)));
        await Eventually(() => rows.Count.Should().Be(2));

        var updatable = rows.Items.Single(row => NameOf(row).EndsWith("Updatable"));
        var frozen = rows.Items.Single(row => NameOf(row).EndsWith("Frozen"));
        using var updatableVersions = ChildrenOf(updatable);
        using var frozenVersions = ChildrenOf(frozen);
        await Eventually(() => (updatableVersions.Count, frozenVersions.Count).Should().Be((1, 1)));
        var rowsToCheck = new[] { updatable, updatableVersions.Items.Single(), frozen, frozenVersions.Items.Single() };
        await OnUi(() =>
        {
            foreach (var row in rowsToCheck) row.Activate();
            return Task.CompletedTask;
        });

        var checker = Provider.GetRequiredService<ThunderstoreUpdateChecker>();
        checker.Refresh(CatalogWith(community, team, "Updatable", "1.0.0", "1.1.0"));

        await EventuallyOnUi(() =>
        {
            foreach (var row in new[] { updatable, updatableVersions.Items.Single() })
            {
                row.GetOptional<LibraryComponents.NewVersionAvailable>(LibraryColumns.ItemVersion.NewVersionComponentKey)
                    .Value.NewVersion.Value.Should().Be("1.1.0");
            }
        });

        // The update arrived for the sibling, so by now the frozen row has had its chance to show one.
        checker.Refresh(CatalogWith(community, team, "Frozen", "1.0.0", "1.1.0"));
        await Task.Delay(500);
        await OnUi(() =>
        {
            foreach (var row in new[] { frozen, frozenVersions.Items.Single() })
            {
                row.GetOptional<LibraryComponents.NewVersionAvailable>(LibraryColumns.ItemVersion.NewVersionComponentKey)
                    .HasValue.Should().BeFalse();
            }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task LocalFileProvider_DoesNotListAThunderstoreFile()
    {
        var community = UniqueCommunity();
        var fileId = await AddFile(community, "Zehs", "REPOLib", "4.1.0");
        var game = ThunderstoreGameOf(community);
        var localFiles = Provider.GetServices<ILibraryDataProvider>().Single(p => p.GetType().Name == "LocalFileDataProvider");

        using var rows = Rows(localFiles.ObserveLibraryItems(new LibraryFilter(NoLoadout, game)));
        await Task.Delay(250);

        rows.Keys.Should().NotContain(fileId);
        localFiles.GetAllFiles(game.GameId).Should().BeEmpty();
    }

    [Fact]
    public void ThunderstoreProvider_IsRegisteredForTheLibraryAndTheLoadout()
    {
        Provider.GetServices<ILibraryDataProvider>().Should().ContainSingle(p => p is ThunderstoreDataProvider);
        Provider.GetServices<ILoadoutDataProvider>().Should().ContainSingle(p => p is ThunderstoreDataProvider);
    }

    // A loadout with the user collection that installed mods go into, like the one a new loadout gets.
    private async Task<(LoadoutId Loadout, EntityId Collection)> AddLoadout(IGameData game)
    {
        using var tx = Connection.BeginTransaction();
        var metadata = new GameInstallMetadata.New(tx)
        {
            Path = "/nonexistent/" + Guid.NewGuid().ToString("N"),
            Name = game.DisplayName,
            Store = GameStore.Unknown,
            Game = game.GameId,
        };
        var loadout = new Loadout.New(tx)
        {
            Name = "Thunderstore loadout",
            ShortName = "TSL",
            InstallationId = metadata,
            LoadoutKind = LoadoutKind.Default,
            Revision = 0,
            GameVersion = VanityVersion.From("Unknown"),
        };
        _ = new CollectionGroup.New(tx, out var collectionId)
        {
            IsReadOnly = false,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, collectionId)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, collectionId) { Name = "My Mods", LoadoutId = loadout },
            },
        };
        var result = await tx.Commit();
        return (result.Remap(loadout).LoadoutId, result[collectionId]);
    }

    private async Task<EntityId> Install((LoadoutId Loadout, EntityId Collection) target, EntityId fileId, string name)
    {
        using var tx = Connection.BeginTransaction();
        var group = new LoadoutItemGroup.New(tx, out var id)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, id) { Name = name, LoadoutId = target.Loadout, ParentId = target.Collection },
        };
        _ = new LibraryLinkedLoadoutItem.New(tx, id) { LibraryItemId = fileId, LoadoutItemGroup = group };
        var result = await tx.Commit();
        return result[id];
    }

    private static LoadoutFilter WholeLoadout(LoadoutId loadout) => new()
    {
        LoadoutId = loadout,
        CollectionGroupId = Optional<LoadoutItemGroupId>.None,
    };

    [Fact]
    public async Task LoadoutItems_AreOneParentPerInstalledPackageWithItsLoadoutItemsUnderneath()
    {
        var community = UniqueCommunity();
        var game = ThunderstoreGameOf(community);
        var loadout = await AddLoadout(game);
        var older = await AddFile(community, "Zehs", "REPOLib", "4.1.0");
        var newer = await AddFile(community, "Zehs", "REPOLib", "4.2.0");
        var config = await AddFile(community, "nickklmao", "REPOConfig", "1.2.6");
        await AddFile(community, "Someone", "DownloadedOnly", "1.0.0");
        var olderItem = await Install(loadout, older, "REPOLib 4.1.0");
        var newerItem = await Install(loadout, newer, "REPOLib 4.2.0");
        await Install(loadout, config, "REPOConfig");
        var thunderstore = CreateProvider(game);
        var count = -1;

        using var rows = Rows(thunderstore.ObserveLoadoutItems(WholeLoadout(loadout.Loadout)));
        using var _ = thunderstore.CountLoadoutItems(WholeLoadout(loadout.Loadout)).Subscribe(value => count = value);

        await Eventually(() => rows.Count.Should().Be(2));
        rows.Items.Select(NameOf).Should().BeEquivalentTo("REPOLib", "REPOConfig");
        await Eventually(() => count.Should().Be(2));

        var repoLib = rows.Items.Single(row => NameOf(row) == "REPOLib");
        using var items = ChildrenOf(repoLib);
        await Eventually(() => items.Keys.Should().BeEquivalentTo([olderItem, newerItem]));
        items.Items.Select(row => row.Get<StringComponent>(LoadoutColumns.Collections.ComponentKey).Value.Value)
            .Should().AllBe("My Mods");
    }

    [Fact]
    public async Task ALoadoutOfAGameWithoutThunderstoreSupport_ShowsNothingAndCountsZero()
    {
        var community = UniqueCommunity();
        var game = PlainGame();
        var loadout = await AddLoadout(game);
        await Install(loadout, await AddFile(community, "Zehs", "REPOLib", "4.1.0"), "REPOLib");
        var thunderstore = CreateProvider(game);
        var count = -1;

        using var rows = Rows(thunderstore.ObserveLoadoutItems(WholeLoadout(loadout.Loadout)));
        using var _ = thunderstore.CountLoadoutItems(WholeLoadout(loadout.Loadout)).Subscribe(value => count = value);

        await Eventually(() => count.Should().Be(0));
        rows.Count.Should().Be(0);
    }

    private async Task DeleteFile(EntityId id)
    {
        using var tx = Connection.BeginTransaction();
        tx.Delete(id, recursive: true);
        await tx.Commit();
    }

    [Fact]
    public async Task DeletingTheOldestVersion_KeepsTheParentRowAndItsKey()
    {
        var community = UniqueCommunity();
        var older = await AddFile(community, "Zehs", "REPOLib", "4.1.0");
        var newer = await AddFile(community, "Zehs", "REPOLib", "4.2.0");
        var game = ThunderstoreGameOf(community);
        var filter = new LibraryFilter(NoLoadout, game);
        var thunderstore = CreateProvider(game);

        using var rows = Rows(thunderstore.ObserveLibraryItems(filter));
        await Eventually(() => rows.Count.Should().Be(1));
        var parent = rows.Items.Single();
        var keyBefore = parent.Key;
        keyBefore.Should().NotBe(older).And.NotBe(newer, "the row stands for the package, not for one of its files");

        using var versions = ChildrenOf(parent);
        await Eventually(() => versions.Keys.Should().BeEquivalentTo([older, newer]));

        await DeleteFile(older);

        await Eventually(() => versions.Keys.Should().BeEquivalentTo([newer]));
        rows.Items.Should().ContainSingle().Which.Key.Should().Be(keyBefore);

        // A row built after the deletion is keyed the same as the one built before it.
        using var rebuilt = Rows(thunderstore.ObserveLibraryItems(filter));
        await Eventually(() => rebuilt.Count.Should().Be(1));
        rebuilt.Items.Single().Key.Should().Be(keyBefore);
    }

    [Fact]
    public async Task DeletingTheLastVersion_RemovesTheParentRow()
    {
        var community = UniqueCommunity();
        var only = await AddFile(community, "Zehs", "REPOLib", "4.1.0");
        var game = ThunderstoreGameOf(community);
        var thunderstore = CreateProvider(game);

        using var rows = Rows(thunderstore.ObserveLibraryItems(new LibraryFilter(NoLoadout, game)));
        await Eventually(() => rows.Count.Should().Be(1));

        await DeleteFile(only);

        await Eventually(() => rows.Count.Should().Be(0));
    }

    [Fact]
    public async Task EachPackage_GetsItsOwnParentKey()
    {
        var community = UniqueCommunity();
        await AddFile(community, "Zehs", "REPOLib", "4.1.0");
        await AddFile(community, "nickklmao", "REPOConfig", "1.2.6");
        var game = ThunderstoreGameOf(community);
        var thunderstore = CreateProvider(game);

        using var rows = Rows(thunderstore.ObserveLibraryItems(new LibraryFilter(NoLoadout, game)));

        await Eventually(() => rows.Count.Should().Be(2));
    }
}
