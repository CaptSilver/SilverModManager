using System.Reflection;
using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Backend;
using SilverModManager.Library.Tests.DownloadsService;
using SilverModManager.Library.Tests.Lock;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.NexusWebApi;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.NexusModsApi;
using Xunit;

namespace SilverModManager.Library.Tests.ModSources;

/// <summary>
/// What <c>NexusModSource</c> reports as updates, with the update service substituted: the source only maps what
/// the service says into <see cref="ModUpdate"/>s.
/// </summary>
public class NexusModSourceUpdateTests(IConnection connection)
{
    // The host's database is shared by every test in the run, so each test gets mod and file ids of its own.
    private static uint _nextId = 72000;

    private async Task<(LibraryFile.ReadOnly Library, NexusModsFileMetadata.ReadOnly Current, NexusModsFileMetadata.ReadOnly Newer)> AddFileWithNewerVersion(string currentVersion = "1.0", string newerVersion = "2.0", DateTimeOffset? newerUploadedAt = null)
    {
        var fixtures = new LockFixtures(connection);
        var id = _nextId++;
        var library = await fixtures.AddNexusFile("mod.zip", "nexus " + id, null, "A mod", currentVersion, id, id);
        var current = NexusModsLibraryItem.Load(connection.Db, library.Id).FileMetadata;
        var newer = await AddNewerFile(current, newerVersion, newerUploadedAt ?? DateTimeOffset.UnixEpoch.AddDays(10), fileId: _nextId++);
        return (library, current, newer);
    }

    private async Task<NexusModsFileMetadata.ReadOnly> AddNewerFile(NexusModsFileMetadata.ReadOnly sibling, string version, DateTimeOffset uploadedAt, uint fileId)
    {
        using var tx = connection.BeginTransaction();
        var metadata = new NexusModsFileMetadata.New(tx)
        {
            Uid = new FileUid(FileId.From(fileId), LockFixtures.GameId),
            Name = "newer.zip",
            Version = version,
            UploadedAt = uploadedAt.UtcDateTime,
            ModPageId = sibling.ModPageId,
        };
        var result = await tx.Commit();
        return result.Remap(metadata);
    }

    // NSubstitute can't configure GameId: reading the unset property makes it construct one, and GameId refuses.
    private class GameData : DispatchProxy
    {
        public GameId Id { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name == "get_GameId" ? Id : throw new NotSupportedException(targetMethod?.Name);
    }

    private static IGameData GameWithId(string id)
    {
        var game = DispatchProxy.Create<IGameData, GameData>();
        ((GameData)game).Id = GameId.From(id);
        return game;
    }

    private static T Latest<T>(IObservable<T> observable)
    {
        T latest = default!;
        var emitted = false;
        using var _ = observable.Subscribe(value => (latest, emitted) = (value, true));
        emitted.Should().BeTrue("the observable should have a value on subscribing");
        return latest;
    }

    [Fact]
    public async Task ObserveUpdate_ForAFile_IsAnUpdateOfOneItemWithBothVersions()
    {
        var (library, current, newer) = await AddFileWithNewerVersion();
        var service = Substitute.For<IModUpdateService>();
        service.GetNewestFileVersionObservable(Arg.Any<NexusModsFileMetadata.ReadOnly>(), Arg.Any<Func<ModUpdateOnPage, ModUpdateOnPage?>>())
            .Returns(System.Reactive.Linq.Observable.Return(Optional.Some(new ModUpdateOnPage(current, [newer]))));

        var update = Latest(NexusModSources.Create(connection, service).ObserveUpdate(library.AsLibraryItem()));

        update.HasValue.Should().BeTrue();
        update.Value.Count.Should().Be(1);
        update.Value.CurrentVersion.Should().Be("1.0");
        update.Value.NewestVersion.Should().Be("2.0");
        update.Value.CurrentItems.Should().Equal(library.AsLibraryItem().LibraryItemId);
        update.Value.PublishedAt.Should().Be(newer.UploadedAt);
    }

    [Fact]
    public async Task ObserveUpdate_ForAFile_IsNoneWhenTheServiceHasNoUpdate()
    {
        var (library, _, _) = await AddFileWithNewerVersion();
        var service = Substitute.For<IModUpdateService>();
        service.GetNewestFileVersionObservable(Arg.Any<NexusModsFileMetadata.ReadOnly>(), Arg.Any<Func<ModUpdateOnPage, ModUpdateOnPage?>>())
            .Returns(System.Reactive.Linq.Observable.Return(Optional<ModUpdateOnPage>.None));

        Latest(NexusModSources.Create(connection, service).ObserveUpdate(library.AsLibraryItem())).HasValue.Should().BeFalse();
    }

    [Fact]
    public async Task ObserveUpdate_ForAnItemNexusDoesNotOwn_IsNone()
    {
        var local = await new LockFixtures(connection).AddLibraryFile("local.zip", "local " + _nextId++, null);

        Latest(NexusModSources.Create(connection, Substitute.For<IModUpdateService>()).ObserveUpdate(local.AsLibraryItem())).HasValue.Should().BeFalse();
    }

    [Fact]
    public async Task ObserveUpdate_ForAPage_TakesTheCurrentVersionFromTheFileWithTheNewestUpdate()
    {
        var (library, current, newer) = await AddFileWithNewerVersion(currentVersion: "1.0", newerVersion: "2.0", newerUploadedAt: DateTimeOffset.UnixEpoch.AddDays(10));
        var otherLibrary = await new LockFixtures(connection).AddNexusFile("other.zip", "nexus other " + _nextId, null, "A mod", "0.5", _nextId, _nextId);
        var other = NexusModsLibraryItem.Load(connection.Db, otherLibrary.Id).FileMetadata;
        var newest = await AddNewerFile(other, "0.9", DateTimeOffset.UnixEpoch.AddDays(30), fileId: _nextId + 100);
        var page = NexusModsLibraryItem.Load(connection.Db, library.Id).ModPageMetadata;
        var service = Substitute.For<IModUpdateService>();
        service.GetNewestModPageVersionObservable(Arg.Any<NexusModsModPageMetadata.ReadOnly>(), Arg.Any<Func<ModUpdatesOnModPage, ModUpdatesOnModPage?>>())
            .Returns(System.Reactive.Linq.Observable.Return(Optional.Some(new ModUpdatesOnModPage([
                new ModUpdateOnPage(current, [newer]),
                new ModUpdateOnPage(other, [newest]),
            ]))));

        var update = Latest(NexusModSources.Create(connection, service).ObserveUpdate([library.AsLibraryItem(), otherLibrary.AsLibraryItem()]));

        update.HasValue.Should().BeTrue();
        update.Value.Count.Should().Be(2);
        update.Value.CurrentVersion.Should().Be("0.5", "the row shows the file whose update is newest");
        update.Value.NewestVersion.Should().Be("0.9");
        update.Value.CurrentItems.Should().BeEquivalentTo([library.AsLibraryItem().LibraryItemId, otherLibrary.AsLibraryItem().LibraryItemId]);
        service.Received().GetNewestModPageVersionObservable(Arg.Is<NexusModsModPageMetadata.ReadOnly>(p => p.Id == page.Id), Arg.Any<Func<ModUpdatesOnModPage, ModUpdatesOnModPage?>>());
    }

    [Fact]
    public async Task ObserveUpdate_ForAPageWithNoFileMappings_IsNone()
    {
        var (library, _, _) = await AddFileWithNewerVersion();
        var service = Substitute.For<IModUpdateService>();
        service.GetNewestModPageVersionObservable(Arg.Any<NexusModsModPageMetadata.ReadOnly>(), Arg.Any<Func<ModUpdatesOnModPage, ModUpdatesOnModPage?>>())
            .Returns(System.Reactive.Linq.Observable.Return(Optional.Some(new ModUpdatesOnModPage([]))));

        Latest(NexusModSources.Create(connection, service).ObserveUpdate([library.AsLibraryItem()])).HasValue.Should().BeFalse();
    }

    [Fact]
    public void ObserveUpdate_ForNoItems_IsNone()
    {
        Latest(NexusModSources.Create(connection, Substitute.For<IModUpdateService>()).ObserveUpdate(Array.Empty<LibraryItem.ReadOnly>())).HasValue.Should().BeFalse();
    }

    [Fact]
    public async Task AllUpdates_AsksTheServiceForTheGamesPagesOnly()
    {
        var (library, current, newer) = await AddFileWithNewerVersion();
        var page = NexusModsLibraryItem.Load(connection.Db, library.Id).ModPageMetadata;
        var gameA = GameWithId("update-game-a");
        var gameB = GameWithId("update-game-b");
        var service = Substitute.For<IModUpdateService>();
        service.GetAllModPagesWithUpdates(gameA.GameId, Arg.Any<Func<ModUpdatesOnModPage, ModUpdatesOnModPage?>>())
            .Returns([(page.Id, new ModUpdatesOnModPage([new ModUpdateOnPage(current, [newer])]))]);
        service.GetAllModPagesWithUpdates(gameB.GameId, Arg.Any<Func<ModUpdatesOnModPage, ModUpdatesOnModPage?>>())
            .Returns([]);
        var source = NexusModSources.Create(connection, service);

        var forA = source.AllUpdates(gameA);
        var forB = source.AllUpdates(gameB);

        forA.Should().ContainSingle().Which.NewestVersion.Should().Be("2.0");
        forA[0].CurrentItems.Should().Equal(library.AsLibraryItem().LibraryItemId);
        forB.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ObserveHasAnyUpdates_FollowsTheServicesGlobalState(bool hasAny)
    {
        var service = Substitute.For<IModUpdateService>();
        service.HasAnyUpdatesObservable(Arg.Any<Func<ModUpdatesOnModPage, ModUpdatesOnModPage?>>())
            .Returns(System.Reactive.Linq.Observable.Return(hasAny));

        Latest(NexusModSources.Create(connection, service).ObserveHasAnyUpdates(GameWithId("update-game-c"))).Should().Be(hasAny);
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
