using SilverModManager.Backend;
using SilverModManager.Sdk.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk;
using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SilverModManager.Abstractions.Library;
using SilverModManager.Library.GameBanana;
using SilverModManager.Library.Listing;
using SilverModManager.Library.Tests.DownloadsService;
using SilverModManager.Library.Tests.GitHub;
using SilverModManager.Library.Tests.Lock;
using SilverModManager.Library.Tests.ModSources;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.GameBanana;
using SilverModManager.Networking.GameBanana.DTOs;
using SilverModManager.Sdk.Library;
using Xunit;

namespace SilverModManager.Library.Tests.GameBanana;

internal static class GameBananaModSources
{
    public static GameBananaModSource Create(GameBananaRig rig, IServiceProvider provider, ILibraryService? library = null)
    {
        var services = new ServiceCollection().AddSingleton(library ?? Substitute.For<ILibraryService>()).BuildServiceProvider();
        return new GameBananaModSource(rig.Profiles, rig.Connection, new CompositeServiceProvider(services, provider), () => rig.Client, NullLogger<GameBananaModSource>.Instance);
    }

    public static async Task<LibraryItem.ReadOnly> AddItem(IConnection connection, long modId, long fileId, long dateUpdated, string? name = null, int gameId = 297)
    {
        var content = "content " + Guid.NewGuid();
        var file = await new LockFixtures(connection).AddGameBananaFile(name ?? $"mod-{fileId}.zip", content, LockFixtures.DigestOf(content), modId, fileId, dateUpdated, gameId);
        return file.AsLibraryItem();
    }
}

public class GameBananaModSourceTests(IServiceProvider serviceProvider, IConnection connection)
{
    // The tests share a database, so each one lists a mod of its own.
    private readonly long _modId = Random.Shared.NextInt64(1_000_000, 900_000_000);
    private readonly int _gameId = Random.Shared.Next(1_000_000, int.MaxValue);
    private long FileId(int n) => _modId * 10 + n;

    private static Optional<ModUpdate> LatestOf(IObservable<Optional<ModUpdate>> observed)
    {
        var seen = Optional<ModUpdate>.None;
        using (observed.Subscribe(value => seen = value)) { }
        return seen;
    }

    private GameBananaRig Arrange(long? updated, long modified, params long[] fileNumbers)
    {
        var rig = new GameBananaRig(connection, gameBananaId: _gameId);
        rig.Api.Profiles[_modId] = TestMods.Profile(_modId, "Mod", updated, modified, [.. fileNumbers.Select(n => TestMods.File(FileId((int)n), $"mod-{n}.zip", added: 100 + n))], gameId: _gameId);
        return rig;
    }

    [Fact]
    public async Task ADescriptionEditDoesNotCountAsAnUpdate()
    {
        // The stored snapshot: updated at 1000, one file. The page's modified stamp has moved on, its updated stamp
        // and its files have not.
        var rig = Arrange(updated: 1000, modified: 9999, 1);
        var item = await GameBananaModSources.AddItem(connection, _modId, FileId(1), dateUpdated: 1000, gameId: _gameId);
        var source = GameBananaModSources.Create(rig, serviceProvider);

        await source.CheckUpdates(rig.Game, force: false, CancellationToken.None);

        LatestOf(source.ObserveUpdate(item)).HasValue.Should().BeFalse();
        source.AllUpdates(rig.Game).Should().BeEmpty();
    }

    [Fact]
    public async Task ANewFileWithANewerUpdatedStampIsAnUpdate()
    {
        var rig = Arrange(updated: 2000, modified: 2000, 1, 2);
        var item = await GameBananaModSources.AddItem(connection, _modId, FileId(1), dateUpdated: 1000, gameId: _gameId);
        var source = GameBananaModSources.Create(rig, serviceProvider);

        await source.CheckUpdates(rig.Game, force: false, CancellationToken.None);

        var update = LatestOf(source.ObserveUpdate(item)).Value;
        update.NewestVersion.Should().Be("updated on 1970-01-01", "a page without a version says when it was updated");
        update.PublishedAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(2000));
        update.Count.Should().Be(1);
        source.Owns(update).Should().BeTrue();
        source.AllUpdates(rig.Game).Should().ContainSingle();
        bool? any = null;
        using (source.ObserveHasAnyUpdates(rig.Game).Subscribe(value => any = value)) { }
        any.Should().BeTrue();
    }

    [Fact]
    public async Task ANewerStampWithoutANewFileIsNotAnUpdate()
    {
        var rig = Arrange(updated: 2000, modified: 2000, 1);
        var item = await GameBananaModSources.AddItem(connection, _modId, FileId(1), dateUpdated: 1000, gameId: _gameId);
        var source = GameBananaModSources.Create(rig, serviceProvider);

        await source.CheckUpdates(rig.Game, force: false, CancellationToken.None);

        LatestOf(source.ObserveUpdate(item)).HasValue.Should().BeFalse();
    }

    [Fact]
    public async Task ANewFileWithoutANewerStampIsNotAnUpdate()
    {
        // The modified stamp moves with the new file; only the updated stamp says the author published an update.
        var rig = Arrange(updated: 1000, modified: 9999, 1, 2);
        var item = await GameBananaModSources.AddItem(connection, _modId, FileId(1), dateUpdated: 1000, gameId: _gameId);
        var source = GameBananaModSources.Create(rig, serviceProvider);

        await source.CheckUpdates(rig.Game, force: false, CancellationToken.None);

        LatestOf(source.ObserveUpdate(item)).HasValue.Should().BeFalse("the author added a file without publishing an update");
    }

    [Fact]
    public async Task APageWithAVersionNamesItInTheUpdate()
    {
        var rig = new GameBananaRig(connection, gameBananaId: _gameId);
        rig.Api.Profiles[_modId] = TestMods.Profile(_modId, "Mod", 2000, 2000, [TestMods.File(FileId(1), "a.zip"), TestMods.File(FileId(2), "b.zip", added: 5)], version: "1.4", gameId: _gameId);
        var item = await GameBananaModSources.AddItem(connection, _modId, FileId(1), dateUpdated: 1000, gameId: _gameId);
        var source = GameBananaModSources.Create(rig, serviceProvider);

        await source.CheckUpdates(rig.Game, force: false, CancellationToken.None);

        LatestOf(source.ObserveUpdate(item)).Value.NewestVersion.Should().Be("1.4");
    }

    [Fact]
    public async Task AFileOfAnotherGameIsNotCheckedForThisOne()
    {
        var rig = Arrange(updated: 2000, modified: 2000, 1, 2);
        await GameBananaModSources.AddItem(connection, _modId, FileId(1), dateUpdated: 1000, gameId: 1234);
        var source = GameBananaModSources.Create(rig, serviceProvider);

        await source.CheckUpdates(rig.Game, force: false, CancellationToken.None);

        rig.Api.Requests.Should().BeEmpty();
        source.AllUpdates(rig.Game).Should().BeEmpty();
    }

    [Fact]
    public async Task AFileRestoredFromALockWithoutItsModPageIsNeverChecked()
    {
        var rig = Arrange(updated: 2000, modified: 2000, 1, 2);
        var item = await GameBananaModSources.AddItem(connection, modId: 0, FileId(1), dateUpdated: 0, gameId: _gameId);
        var source = GameBananaModSources.Create(rig, serviceProvider);

        await source.CheckUpdates(rig.Game, force: false, CancellationToken.None);

        rig.Api.Requests.Should().BeEmpty();
        source.PageUri(item).Should().BeNull();
        LatestOf(source.ObserveUpdate(item)).HasValue.Should().BeFalse();
    }

    [Fact]
    public async Task CheckUpdatesWithoutForceWithinTheHourMakesNoRequest()
    {
        var rig = Arrange(updated: 1000, modified: 1000, 1);
        await GameBananaModSources.AddItem(connection, _modId, FileId(1), dateUpdated: 1000, gameId: _gameId);
        var source = GameBananaModSources.Create(rig, serviceProvider);

        await source.CheckUpdates(rig.Game, force: false, CancellationToken.None);
        rig.Time.Advance(TimeSpan.FromMinutes(30));
        await source.CheckUpdates(rig.Game, force: false, CancellationToken.None);
        rig.Api.Requests.Should().Equal($"profile {_modId}");

        await source.CheckUpdates(rig.Game, force: true, CancellationToken.None);
        rig.Api.Requests.Should().HaveCount(2, "Refresh asks again");
    }

    [Fact]
    public async Task AModThatWentPrivateOrAnAnsweringLimitLeavesTheCheckQuietAndKeepsWhatWasKnown()
    {
        var rig = Arrange(updated: 2000, modified: 2000, 1, 2);
        var item = await GameBananaModSources.AddItem(connection, _modId, FileId(1), dateUpdated: 1000, gameId: _gameId);
        var source = GameBananaModSources.Create(rig, serviceProvider);
        await source.CheckUpdates(rig.Game, force: false, CancellationToken.None);
        rig.Api.Failure = new GameBananaRateLimitedException(TimeSpan.FromMinutes(5));

        await source.CheckUpdates(rig.Game, force: true, CancellationToken.None);

        LatestOf(source.ObserveUpdate(item)).HasValue.Should().BeTrue("the earlier answer still stands");
    }

    [Fact]
    public async Task AFrozenItemHasNoUpdate()
    {
        var rig = Arrange(updated: 2000, modified: 2000, 1, 2);
        var item = await GameBananaModSources.AddItem(connection, _modId, FileId(1), dateUpdated: 1000, gameId: _gameId);
        var source = GameBananaModSources.Create(rig, serviceProvider);
        await source.CheckUpdates(rig.Game, force: false, CancellationToken.None);
        LatestOf(source.ObserveUpdate(item)).HasValue.Should().BeTrue();

        await source.SetUpdatesHidden([item], hidden: true, CancellationToken.None);

        LatestOf(source.ObserveUpdate(item)).HasValue.Should().BeFalse();
        HideStateOf(source, item).Should().Be(new HideUpdatesState(IsHidden: true, ItemCount: 1, IsEnabled: true, IsVisible: true));
        source.HideUpdatesLabel(hidden: false).Should().Be(HideUpdatesLabelKind.Freeze);
        source.HideUpdatesLabel(hidden: true).Should().Be(HideUpdatesLabelKind.Unfreeze);

        await source.SetUpdatesHidden([item], hidden: false, CancellationToken.None);
        LatestOf(source.ObserveUpdate(item)).HasValue.Should().BeTrue();
    }

    private static HideUpdatesState HideStateOf(IModSource source, LibraryItem.ReadOnly item)
    {
        HideUpdatesState? state = null;
        using (source.ObserveHideUpdates([item]).Subscribe(value => state = value)) { }
        return state!.Value;
    }

    [Fact]
    public async Task OnlyItemsDownloadedFromGameBananaAreOwned()
    {
        var rig = Arrange(updated: 1000, modified: 1000, 1);
        var source = GameBananaModSources.Create(rig, serviceProvider);
        var banana = await GameBananaModSources.AddItem(connection, _modId, FileId(1), dateUpdated: 1000, gameId: _gameId);
        var other = (await new LockFixtures(connection).AddLibraryFile("local.zip", "x" + Guid.NewGuid(), null)).AsLibraryItem();

        source.Owns(banana).Should().BeTrue();
        source.Owns(other).Should().BeFalse();
        source.DisplayName.Should().Be("GameBanana");
        source.PageUri(banana).Should().Be(new Uri($"https://gamebanana.com/mods/{_modId}"));
        source.PageUri(other).Should().BeNull();
    }

    [Fact]
    public async Task ApplyingAnUpdateDownloadsTheNewFileAndSwapsItIntoTheLoadouts()
    {
        var payload = Guid.NewGuid().ToByteArray().Concat(Enumerable.Range(0, 2000).Select(i => (byte)(i * 3 + 1))).ToArray();
        var rig = new GameBananaRig(connection, gameBananaId: _gameId);
        rig.Api.Profiles[_modId] = TestMods.Profile(_modId, "Mod", 2000, 2000,
            [TestMods.File(FileId(1), "old.zip"), TestMods.File(FileId(2), $"new-{_modId}.zip", Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(payload)), added: 9)], version: "2.0", gameId: _gameId);
        rig.Bytes.Serve($"https://gamebanana.com/dl/{FileId(2)}", payload);
        var item = await GameBananaModSources.AddItem(connection, _modId, FileId(1), dateUpdated: 1000, gameId: _gameId);
        var library = Substitute.For<ILibraryService>();
        library.ReplaceLinkedItemsInAllLoadouts(Arg.Any<LibraryItem.ReadOnly>(), Arg.Any<LibraryItem.ReadOnly>(), Arg.Any<ReplaceLibraryItemOptions>())
            .Returns(ValueTask.FromResult(LibraryItemReplacementResult.Success));
        var source = GameBananaModSources.Create(rig, serviceProvider, library);
        await source.CheckUpdates(rig.Game, force: false, CancellationToken.None);
        var update = source.AllUpdates(rig.Game).Single();

        var outcome = await source.ApplyUpdates(new ModUpdateRequest([update], Replace: true, MayOpenBrowser: true), CancellationToken.None);

        outcome.DownloadFailures.Should().BeEmpty();
        var downloaded = outcome.Downloaded.Should().ContainSingle().Subject;
        var model = GameBananaLibraryFile.Load(connection.Db, downloaded.Id);
        (model.FileId, model.DateUpdated).Should().Be((FileId(2), 2000L));
        model.ListedVersion.Value.Should().Be("2.0");
        outcome.Replacements.Should().ContainSingle().Which.Old.Id.Should().Be(item.Id);
        await library.Received(1).ReplaceLinkedItemsInAllLoadouts(
            Arg.Is<LibraryItem.ReadOnly>(old => old.Id == item.Id), Arg.Is<LibraryItem.ReadOnly>(next => next.Id == downloaded.Id),
            Arg.Is<ReplaceLibraryItemOptions>(options => options == ReplaceLibraryItemOptions.ModUpdate));
        outcome.OpenedInBrowser.Should().BeEmpty();
    }

    [Fact]
    public async Task AFailedDownloadComesBackInTheOutcomeInsteadOfBeingThrown()
    {
        var rig = Arrange(updated: 2000, modified: 2000, 1, 2); // the new file is not served, so the download fails
        await GameBananaModSources.AddItem(connection, _modId, FileId(1), dateUpdated: 1000, gameId: _gameId);
        var source = GameBananaModSources.Create(rig, serviceProvider);
        await source.CheckUpdates(rig.Game, force: false, CancellationToken.None);

        var outcome = await source.ApplyUpdates(new ModUpdateRequest(source.AllUpdates(rig.Game), Replace: true, MayOpenBrowser: true), CancellationToken.None);

        outcome.DownloadFailures.Should().ContainSingle();
        outcome.Downloaded.Should().BeEmpty();
    }

    [Fact]
    public async Task ANewFileTheScanFlaggedIsReportedAsAFailureNotDownloaded()
    {
        var rig = new GameBananaRig(connection, gameBananaId: _gameId);
        rig.Api.Profiles[_modId] = TestMods.Profile(_modId, "Mod", 2000, 2000, [TestMods.File(FileId(1), "old.zip"), TestMods.File(FileId(2), "new.zip", clean: false, added: 9)], gameId: _gameId);
        await GameBananaModSources.AddItem(connection, _modId, FileId(1), dateUpdated: 1000, gameId: _gameId);
        var source = GameBananaModSources.Create(rig, serviceProvider);
        await source.CheckUpdates(rig.Game, force: false, CancellationToken.None);

        var outcome = await source.ApplyUpdates(new ModUpdateRequest(source.AllUpdates(rig.Game), Replace: true, MayOpenBrowser: false), CancellationToken.None);

        outcome.DownloadFailures.Should().ContainSingle().Which.Error.Should().BeOfType<GameBananaRefusedException>();
        rig.Bytes.Fetched.Should().BeEmpty();
        outcome.OpenedInBrowser.Should().BeEmpty();
    }

    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            DIHelpers.ConfigureServices(services);
            services.AddLibraryModels().AddGameInstallMetadataModel().AddLoadoutAbstractions().AddOSInterop().AddFileExtractors();
        }
    }
}

public class GameBananaUpdateAllBrowserPolicyTests(IServiceProvider serviceProvider, IConnection connection) : ModSourceBrowserPolicyContract(connection)
{
    protected override async Task<(IModSource Source, ModUpdate Update, Func<IReadOnlyList<Uri>> OpenedByOs)> Arrange()
    {
        var modId = Random.Shared.NextInt64(1_000_000, 900_000_000);
        var payload = Guid.NewGuid().ToByteArray().Concat(new byte[300]).ToArray();
        var gameId = Random.Shared.Next(1_000_000, int.MaxValue);
        var rig = new GameBananaRig(connection, gameBananaId: gameId);
        rig.Api.Profiles[modId] = TestMods.Profile(modId, "Policy", 2000, 2000,
            [TestMods.File(modId * 10 + 1, "p1.zip"), TestMods.File(modId * 10 + 2, $"p2-{modId}.zip", Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(payload)), added: 9)], gameId: gameId);
        rig.Bytes.Serve($"https://gamebanana.com/dl/{modId * 10 + 2}", payload);
        await GameBananaModSources.AddItem(connection, modId, modId * 10 + 1, dateUpdated: 1000, gameId: gameId);
        var library = Substitute.For<ILibraryService>();
        library.ReplaceLinkedItemsInAllLoadouts(default, default, default).ReturnsForAnyArgs(ValueTask.FromResult(LibraryItemReplacementResult.Success));
        var source = GameBananaModSources.Create(rig, serviceProvider, library);
        await source.CheckUpdates(rig.Game, force: false, CancellationToken.None);
        // Downloads go straight to the app's own HTTP client: nothing here can reach the browser.
        return (source, source.AllUpdates(rig.Game).Single(), () => []);
    }

    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            DIHelpers.ConfigureServices(services);
            services.AddLibraryModels().AddGameInstallMetadataModel().AddLoadoutAbstractions().AddOSInterop().AddFileExtractors();
        }
    }
}

public class GameBananaKindTests(IConnection connection)
{
    private static readonly GameBananaKind Kind = new();

    [Fact]
    public async Task TheSourceColumnSaysGameBananaWithTheListedVersion()
    {
        var item = await GameBananaModSources.AddItem(connection, 7, 70, dateUpdated: 1000);
        using (var tx = connection.BeginTransaction())
        {
            tx.Add(item.Id, GameBananaLibraryFile.ListedVersion, "3.1");
            await tx.Commit();
        }

        var match = Kind.Match(LibraryItem.Load(connection.Db, item.Id))!;

        match.Kind.Should().Be(LibraryItemKinds.Other);
        match.Version.Should().Be("3.1");
        match.Host.Should().Be("GameBanana");
    }

    [Fact]
    public async Task ARowWithoutAVersionSaysWhenThePageWasUpdated()
    {
        var item = await GameBananaModSources.AddItem(connection, 8, 80, dateUpdated: 1622678310);

        Kind.Match(LibraryItem.Load(connection.Db, item.Id))!.Version.Should().Be("updated on 2021-06-02");
    }

    [Fact]
    public async Task AnItemFromElsewhereDoesNotMatch()
    {
        var other = (await new LockFixtures(connection).AddLibraryFile("local.zip", "x" + Guid.NewGuid(), null)).AsLibraryItem();

        Kind.Match(other).Should().BeNull();
    }

    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            DIHelpers.ConfigureServices(services);
            services.AddLibraryModels();
        }
    }
}
