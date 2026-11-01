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
using SilverModManager.Networking.NexusWebApi.UpdateFilters;
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
            Name = sibling.Name,  // the same name makes it a version of the same variant
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

    // A filter service that hides exactly the files a test names, and says so on FilterTrigger when that changes.
    private sealed class FilterFake
    {
        public HashSet<FileUid> Hidden { get; } = [];
        public System.Reactive.Subjects.Subject<System.Reactive.Unit> Trigger { get; } = new();
        public IModUpdateFilterService Service { get; }
        public List<FileUid> HideCalls { get; } = [];
        public List<FileUid> ShowCalls { get; } = [];

        public FilterFake()
        {
            Service = Substitute.For<IModUpdateFilterService>();
            Service.HideFilesAsync(Arg.Any<IEnumerable<FileUid>>()).Returns(call => { HideCalls.AddRange(call.Arg<IEnumerable<FileUid>>()); return Task.CompletedTask; });
            Service.ShowFilesAsync(Arg.Any<IEnumerable<FileUid>>()).Returns(call => { ShowCalls.AddRange(call.Arg<IEnumerable<FileUid>>()); return Task.CompletedTask; });
            Service.FilterTrigger.Returns(Trigger);
            Service.IsFileHidden(Arg.Any<FileUid>()).Returns(call => Hidden.Contains(call.Arg<FileUid>()));
        }

        public void Change(Action<HashSet<FileUid>> edit)
        {
            edit(Hidden);
            Trigger.OnNext(System.Reactive.Unit.Default);
        }
    }

    private static IModUpdateService ServiceWithUnfilteredFile(NexusModsFileMetadata.ReadOnly current, params NexusModsFileMetadata.ReadOnly[] newer)
    {
        var service = Substitute.For<IModUpdateService>();
        service.GetNewestFileVersionObservable(Arg.Any<NexusModsFileMetadata.ReadOnly>(), Arg.Any<Func<ModUpdateOnPage, ModUpdateOnPage?>>())
            .Returns(System.Reactive.Linq.Observable.Return(newer.Length == 0 ? Optional<ModUpdateOnPage>.None : Optional.Some(new ModUpdateOnPage(current, newer))));
        return service;
    }

    [Fact]
    public async Task SetUpdatesHidden_HidesEveryVersionOfTheItemsFile()
    {
        var (library, current, newer) = await AddFileWithNewerVersion();
        var filters = new FilterFake();

        await NexusModSources.Create(connection, filters: filters.Service).SetUpdatesHidden([library.AsLibraryItem()], hidden: true, CancellationToken.None);

        filters.HideCalls.Should().BeEquivalentTo([current.Uid, newer.Uid]);
        filters.ShowCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task SetUpdatesHidden_ShowsEveryVersionOfTheItemsFile()
    {
        var (library, _, newer) = await AddFileWithNewerVersion();
        var filters = new FilterFake();

        await NexusModSources.Create(connection, filters: filters.Service).SetUpdatesHidden([library.AsLibraryItem()], hidden: false, CancellationToken.None);

        filters.ShowCalls.Should().Contain(newer.Uid);
        filters.HideCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task SetUpdatesHidden_ForAPage_CoversTheFilesOfEveryItem()
    {
        var (first, _, firstNewer) = await AddFileWithNewerVersion();
        var (second, _, secondNewer) = await AddFileWithNewerVersion();
        var filters = new FilterFake();

        await NexusModSources.Create(connection, filters: filters.Service).SetUpdatesHidden([first.AsLibraryItem(), second.AsLibraryItem()], hidden: true, CancellationToken.None);

        filters.HideCalls.Should().Contain([firstNewer.Uid, secondNewer.Uid]);
    }

    [Fact]
    public async Task SetUpdatesHidden_LeavesOutItemsNexusDoesNotOwn()
    {
        var local = await new LockFixtures(connection).AddLibraryFile("local.zip", "local " + _nextId++, null);
        var filters = new FilterFake();

        await NexusModSources.Create(connection, filters: filters.Service).SetUpdatesHidden([local.AsLibraryItem()], hidden: true, CancellationToken.None);

        filters.HideCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task ObserveHideUpdates_ForAFile_IsVisibleAndEnabledWhileAnUnfilteredUpdateExists()
    {
        var (library, current, newer) = await AddFileWithNewerVersion();
        var source = NexusModSources.Create(connection, ServiceWithUnfilteredFile(current, newer), filters: new FilterFake().Service);

        Latest(source.ObserveHideUpdates([library.AsLibraryItem()])).Should().Be(new HideUpdatesState(IsHidden: false, ItemCount: 1, IsEnabled: true, IsVisible: true));
    }

    [Fact]
    public async Task ObserveHideUpdates_ForAFile_IsHiddenWhenEveryNewerFileIs()
    {
        var (library, current, newer) = await AddFileWithNewerVersion();
        var filters = new FilterFake();
        filters.Hidden.Add(newer.Uid);
        var source = NexusModSources.Create(connection, ServiceWithUnfilteredFile(current, newer), filters: filters.Service);

        var state = Latest(source.ObserveHideUpdates([library.AsLibraryItem()]));

        state.IsHidden.Should().BeTrue("the unfiltered update is still there, so the row can offer to show it");
        state.IsVisible.Should().BeTrue();
        state.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task ObserveHideUpdates_ForAFile_IsDisabledAndInvisibleWithoutAnUpdate()
    {
        var (library, current, _) = await AddFileWithNewerVersion();
        var source = NexusModSources.Create(connection, ServiceWithUnfilteredFile(current), filters: new FilterFake().Service);

        var state = Latest(source.ObserveHideUpdates([library.AsLibraryItem()]));

        state.IsEnabled.Should().BeFalse();
        state.IsVisible.Should().BeFalse();
        state.IsHidden.Should().BeFalse();
    }

    [Fact]
    public async Task ObserveHideUpdates_ForAFile_FollowsTheFilterTrigger()
    {
        var (library, current, newer) = await AddFileWithNewerVersion();
        var filters = new FilterFake();
        var seen = new List<HideUpdatesState>();
        using var subscription = NexusModSources.Create(connection, ServiceWithUnfilteredFile(current, newer), filters: filters.Service)
            .ObserveHideUpdates([library.AsLibraryItem()]).Subscribe(seen.Add);

        filters.Change(hidden => hidden.Add(newer.Uid));

        seen.Select(static state => state.IsHidden).Should().Equal(false, true);
    }

    private async Task<(LibraryFile.ReadOnly Library, NexusModsFileMetadata.ReadOnly Current, NexusModsFileMetadata.ReadOnly Newer, ModUpdateOnPage Mapping)> AddMappedFile()
    {
        var (library, current, newer) = await AddFileWithNewerVersion();
        return (library, current, newer, new ModUpdateOnPage(current, [newer]));
    }

    private static IModUpdateService ServiceWithUnfilteredPage(params ModUpdateOnPage[] mappings)
    {
        var service = Substitute.For<IModUpdateService>();
        service.GetNewestModPageVersionObservable(Arg.Any<NexusModsModPageMetadata.ReadOnly>(), Arg.Any<Func<ModUpdatesOnModPage, ModUpdatesOnModPage?>>())
            .Returns(System.Reactive.Linq.Observable.Return(Optional.Some(new ModUpdatesOnModPage(mappings))));
        return service;
    }

    [Fact]
    public async Task ObserveHideUpdates_ForAPage_CountsTheFilesWhoseUpdatesAreShown()
    {
        var (first, _, _, firstMapping) = await AddMappedFile();
        var (second, _, _, secondMapping) = await AddMappedFile();
        var source = NexusModSources.Create(connection, ServiceWithUnfilteredPage(firstMapping, secondMapping), filters: new FilterFake().Service);

        Latest(source.ObserveHideUpdates([first.AsLibraryItem(), second.AsLibraryItem()]))
            .Should().Be(new HideUpdatesState(IsHidden: false, ItemCount: 2, IsEnabled: true, IsVisible: true));
    }

    [Fact]
    public async Task ObserveHideUpdates_ForAPage_IsHiddenWhenAnyFileHasItsUpdatesHiddenAndCountsThose()
    {
        var (first, _, firstNewer, firstMapping) = await AddMappedFile();
        var (second, _, _, secondMapping) = await AddMappedFile();
        var filters = new FilterFake();
        filters.Hidden.Add(firstNewer.Uid);
        var source = NexusModSources.Create(connection, ServiceWithUnfilteredPage(firstMapping, secondMapping), filters: filters.Service);

        Latest(source.ObserveHideUpdates([first.AsLibraryItem(), second.AsLibraryItem()]))
            .Should().Be(new HideUpdatesState(IsHidden: true, ItemCount: 1, IsEnabled: true, IsVisible: true));
    }

    [Fact]
    public async Task ObserveHideUpdates_ForAPageWithoutMappings_IsDisabledAndInvisible()
    {
        var (first, _, _) = await AddFileWithNewerVersion();
        var second = await new LockFixtures(connection).AddNexusFile("two.zip", "nexus two " + _nextId, null, "A mod", "1.0", _nextId, _nextId);
        var source = NexusModSources.Create(connection, ServiceWithUnfilteredPage(), filters: new FilterFake().Service);

        var state = Latest(source.ObserveHideUpdates([first.AsLibraryItem(), second.AsLibraryItem()]));

        state.IsEnabled.Should().BeFalse();
        state.IsVisible.Should().BeFalse();
    }

    [Fact]
    public async Task ObserveHideUpdates_ForAnItemNexusDoesNotOwn_IsDisabledAndInvisible()
    {
        var local = await new LockFixtures(connection).AddLibraryFile("local.zip", "local " + _nextId++, null);

        var state = Latest(NexusModSources.Create(connection).ObserveHideUpdates([local.AsLibraryItem()]));

        state.IsEnabled.Should().BeFalse();
        state.IsVisible.Should().BeFalse();
    }

    [Fact]
    public void HideUpdatesLabel_NamesTheActionTheItemOffers()
    {
        var source = NexusModSources.Create(connection);

        source.HideUpdatesLabel(hidden: false).Should().Be("Hide updates");
        source.HideUpdatesLabel(hidden: true).Should().Be("Show updates");
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
