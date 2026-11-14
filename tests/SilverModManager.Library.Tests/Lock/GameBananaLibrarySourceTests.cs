using SilverModManager.Sdk.Games;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.Library;
using SilverModManager.Backend;
using SilverModManager.Library.Lock;
using SilverModManager.Library.Sources;
using SilverModManager.Library.Tests.DownloadsService;
using SilverModManager.Library.Tests.GitHub;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using Xunit;

namespace SilverModManager.Library.Tests.Lock;

public class GameBananaLibrarySourceTests(IServiceProvider serviceProvider, IConnection connection)
{
    private readonly LockFixtures _fixtures = new(connection);
    private readonly byte[] _payload = Guid.NewGuid().ToByteArray().Concat(Enumerable.Range(0, 3000).Select(i => (byte)(i * 11 + 1))).ToArray();
    private readonly long _fileId = Random.Shared.NextInt64(1_000_000, 900_000_000);

    private Sha256Value PayloadSha => Sha256Value.From(SHA256.HashData(_payload));

    private GameBananaLibrarySource SourceServing(RoutedBytesHandler bytes)
    {
        var services = new ServiceCollection().AddSingleton(new HttpClient(bytes)).BuildServiceProvider();
        return new GameBananaLibrarySource(new CompositeServiceProvider(services, serviceProvider), new LibraryFileDigests(Substitute.For<IFileStore>(), connection));
    }

    private LibrarySourceContext Context => new(connection, null, null);

    private LibrarySourceEntry EntryFor(string asset) => new("pinned", "1.0", _fileId.ToString(), asset, PayloadSha.ToString(), LockSources.GameBanana);

    [Fact]
    public async Task ALockEntryRoundTripsWithTheFileId()
    {
        var loadout = await _fixtures.CreateLoadout();
        var file = await _fixtures.AddGameBananaFile($"gb-{_fileId}.zip", "banana " + _fileId, LockFixtures.DigestOf("banana " + _fileId), modId: 44334, fileId: _fileId);
        using (var tx = connection.BeginTransaction())
        {
            tx.Add(file.Id, GameBananaLibraryFile.ListedVersion, "1.2");
            await tx.Commit();
        }

        await _fixtures.Link(loadout, file, "Banana mod");
        var digests = new LibraryFileDigests(Substitute.For<IFileStore>(), connection);
        var export = new LockExport([SourceServing(new RoutedBytesHandler())], digests, new CollectionOwnership([]));

        var written = (await export.FromLoadout(loadout, TestContext.Current.CancellationToken)).Write();
        var entry = LockFile.Parse(written).Entries.Should().ContainSingle().Subject;

        entry.Should().Be(new LockEntry("banana-mod", "1.2", _fileId.ToString(), $"gb-{_fileId}.zip", LockFixtures.DigestOf("banana " + _fileId).ToString(), "gamebanana"));
    }

    [Fact]
    public async Task ARowWithoutAnyDigestCannotBePinned()
    {
        var file = await _fixtures.AddGameBananaFile($"nodigest-{_fileId}.zip", "x", pinned: null, modId: 1, fileId: _fileId);
        var item = file.AsLibraryItem();

        var act = async () => await SourceServing(new RoutedBytesHandler()).Describe("g", item, file, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<LockException>();
    }

    [Fact]
    public async Task AnItemFromAnotherSourceIsNotDescribed()
    {
        var file = await _fixtures.AddLibraryFile("local.zip", "x" + _fileId, LockFixtures.DigestOf("x" + _fileId));

        var entry = await SourceServing(new RoutedBytesHandler()).Describe("g", file.AsLibraryItem(), file, TestContext.Current.CancellationToken);

        entry.Should().BeNull();
    }

    [Fact]
    public async Task DownloadFetchesTheStableLinkAndVerifiesTheDigest()
    {
        var bytes = new RoutedBytesHandler().Serve($"https://gamebanana.com/dl/{_fileId}", _payload);
        var asset = $"restored-{_fileId}.zip";

        var file = await SourceServing(bytes).Download(EntryFor(asset), PayloadSha, Context, TestContext.Current.CancellationToken);

        bytes.Fetched.Distinct().Should().Equal($"https://gamebanana.com/dl/{_fileId}");
        file.FileName.ToString().Should().Be(asset);
        file.Sha256.Value.Should().Be(PayloadSha);
        var model = GameBananaLibraryFile.Load(connection.Db, file.Id);
        (model.FileId, model.ModId, model.ListedVersion.Value).Should().Be((_fileId, 0L, "1.0"), "the lock does not carry the mod page, so none is recorded");
    }

    [Fact]
    public async Task DownloadTakesTheModPageFromAnEarlierCopyOfTheSameFile()
    {
        await _fixtures.AddGameBananaFile($"earlier-{_fileId}.zip", "earlier " + _fileId, LockFixtures.DigestOf("earlier " + _fileId), modId: 55, fileId: _fileId);
        var bytes = new RoutedBytesHandler().Serve($"https://gamebanana.com/dl/{_fileId}", _payload);

        var file = await SourceServing(bytes).Download(EntryFor($"again-{_fileId}.zip"), PayloadSha, Context, TestContext.Current.CancellationToken);

        GameBananaLibraryFile.Load(connection.Db, file.Id).ModId.Should().Be(55);
    }

    [Fact]
    public async Task ADigestThatDoesNotMatchLeavesNothingInTheLibrary()
    {
        var bytes = new RoutedBytesHandler().Serve($"https://gamebanana.com/dl/{_fileId}", _payload);
        var asset = $"mismatch-{_fileId}.zip";
        var wrong = Sha256Value.From(SHA256.HashData("other"u8));

        var act = async () => await SourceServing(bytes).Download(EntryFor(asset) with { Sha256 = wrong.ToString() }, wrong, Context, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<SilverModManager.Library.GameBanana.GameBananaIntegrityException>();
        LibraryFile.All(connection.Db).Should().NotContain(file => file.FileName == asset);
    }

    [Fact]
    public void AnEntryWithoutANumericFileIdCannotBeDownloaded()
    {
        var source = SourceServing(new RoutedBytesHandler());

        source.WhyNotDownloadable(EntryFor("a.zip") with { FileId = null }, Context).Should().Contain("file id");
        source.WhyNotDownloadable(EntryFor("a.zip") with { FileId = "abc" }, Context).Should().Contain("file id");
        source.WhyNotDownloadable(EntryFor("a.zip"), Context).Should().BeNull();
    }

    [Fact]
    public void GameBananaIsASourceALockMayName()
    {
        LockSources.All.Should().Contain("gamebanana");
        LockSources.GameBanana.Should().Be("gamebanana");
        SourceServing(new RoutedBytesHandler()).Name.Should().Be("gamebanana");
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
