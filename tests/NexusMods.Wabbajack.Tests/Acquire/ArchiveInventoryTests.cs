using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games.FileHashes;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Sdk.Loadouts;
using NexusMods.Abstractions.NexusModsLibrary;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.IO;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.NexusModsApi;
using NexusMods.Wabbajack.Acquire;
using NexusMods.Wabbajack.Format;
using NexusMods.Wabbajack.Library;

namespace NexusMods.Wabbajack.Tests.Acquire;

public class ArchiveInventoryTests(ITestOutputHelper helper) : AcquireTestBase<ArchiveInventoryTests>(helper)
{
    private ArchiveInventory Inventory(bool premium = false) => new(
        ServiceProvider.GetRequiredService<ILibraryArchiveLocator>(),
        Fakes.Login(premium),
        new FakeMappingCache(),
        Connection,
        FileStore,
        ServiceProvider.GetRequiredService<IFileHashesService>());

    private async Task<ArchivePlanRow> RowFor(ArchiveEntry archive, bool premium = false)
    {
        var loadout = await CreateLoadout();
        var plan = await Inventory(premium).Build(ListWith(archive), Connection.Db, loadout);
        return plan.Rows.Should().ContainSingle().Subject;
    }

    private static readonly NexusState SkyrimSeFile = new("SkyrimSpecialEdition", ModId: 77, FileId: 4242, null, null, null, false);

    /// <summary>A library file the way a download from Nexus Mods leaves it, with the digests and size the test chooses.</summary>
    private async Task<LibraryFile.ReadOnly> AddNexusItem(NexusModsGameId game, uint fileId, Hash? xxHash64, Size? recordedSize)
    {
        using var tx = Connection.BeginTransaction();
        var bytes = System.Text.Encoding.UTF8.GetBytes($"nexus-{game}-{fileId}");
        var file = new LibraryFile.New(tx, out var id)
        {
            FileName = "nexus.7z",
            Hash = bytes.xxHash3(),
            Size = Size.FromLong(bytes.Length),
            LibraryItem = new LibraryItem.New(tx, id) { Name = "nexus.7z" },
        };
        if (xxHash64 is { } digest) tx.Add(id, LibraryFile.XxHash64, digest);

        var page = new NexusModsModPageMetadata.New(tx)
        {
            Uid = new ModUid(ModId.From(77), game),
            Name = "a mod",
            GameDomain = GameDomain.From("skyrimspecialedition"),
            UpdatedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
            DataUpdatedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
        };
        var metadata = new NexusModsFileMetadata.New(tx)
        {
            Uid = new FileUid(FileId.From(fileId), game),
            Name = "nexus.7z",
            Version = "1",
            UploadedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
            ModPageId = page,
        };
        if (recordedSize is { } size) tx.Add(metadata.Id, NexusModsFileMetadata.Size, size);
        tx.Add(id, NexusModsLibraryItem.FileMetadataId, metadata.Id);
        tx.Add(id, NexusModsLibraryItem.ModPageMetadataId, page.Id);
        var result = await tx.Commit();
        return result.Remap(file);
    }

    [Fact]
    public void TheInventoryIsRegisteredWithTheServices()
    {
        ServiceProvider.GetRequiredService<ArchiveInventory>().Should().NotBeNull();
    }

    [Fact]
    public async Task AnArchiveAddedToTheLibraryIsHave()
    {
        await using var zip = await CreateTestArchive(new Dictionary<RelativePath, byte[]> { [RelativePath.FromUnsanitizedInput("a.txt")] = "hello"u8.ToArray() });
        var library = await RegisterLocalArchive(zip.Path);
        var hash = library.AsLibraryFile().XxHash64.Value;

        var row = await RowFor(Entry(new HttpState(new Uri("https://example.invalid/a.zip"), []), hash));

        row.State.Should().Be(ArchiveState.Have);
        row.Library!.Value.Id.Should().Be(library.Id);
    }

    [Fact]
    public async Task ANexusFileWithTheRightIdentityAndRecordedSizeIsHave()
    {
        var item = await AddNexusItem(FakeMappingCache.SkyrimSe, 4242, xxHash64: null, recordedSize: Size.FromLong(1000));

        var row = await RowFor(Entry(SkyrimSeFile, size: 1000));

        row.State.Should().Be(ArchiveState.Have);
        row.Library!.Value.Id.Should().Be(item.Id);
    }

    [Fact]
    public async Task ANexusFileWithADifferentRecordedSizeIsNotHave()
    {
        await AddNexusItem(FakeMappingCache.SkyrimSe, 4242, xxHash64: null, recordedSize: Size.FromLong(999));

        var row = await RowFor(Entry(SkyrimSeFile, size: 1000));

        row.State.Should().Be(ArchiveState.Browser);
        row.Library.Should().BeNull();
    }

    [Fact]
    public async Task ANexusFileWithoutARecordedSizeIsUnmatchedAndBrowserForAFreeAccount()
    {
        await AddNexusItem(FakeMappingCache.SkyrimSe, 4242, xxHash64: null, recordedSize: null);

        var row = await RowFor(Entry(SkyrimSeFile, size: 1000), premium: false);

        row.State.Should().Be(ArchiveState.Browser);
        row.Library.Should().BeNull();
    }

    [Fact]
    public async Task ANexusFileWithoutARecordedSizeIsAutomaticForAPremiumAccount()
    {
        await AddNexusItem(FakeMappingCache.SkyrimSe, 4242, xxHash64: null, recordedSize: null);

        var row = await RowFor(Entry(SkyrimSeFile, size: 1000), premium: true);

        row.State.Should().Be(ArchiveState.Automatic);
        row.Library.Should().BeNull();
    }

    [Fact]
    public async Task ANexusFileWhoseRecordedXxHash64DiffersIsNotHave()
    {
        await AddNexusItem(FakeMappingCache.SkyrimSe, 4242, xxHash64: Hash.From(0x1111), recordedSize: Size.FromLong(1000));

        var row = await RowFor(Entry(SkyrimSeFile, hash: Hash.From(0x2222), size: 1000));

        row.State.Should().Be(ArchiveState.Browser);
    }

    [Fact]
    public async Task ANexusFileWhoseRecordedXxHash64MatchesIsHave()
    {
        await AddNexusItem(FakeMappingCache.SkyrimSe, 4242, xxHash64: Hash.From(0x2222), recordedSize: null);

        var row = await RowFor(Entry(SkyrimSeFile, hash: Hash.From(0x2222), size: 1000));

        row.State.Should().Be(ArchiveState.Have);
    }

    [Fact]
    public async Task ASkyrimDomainStateInASkyrimSeLoadoutUsesTheSkyrimGameIdAndPage()
    {
        // The loadout is Skyrim SE; the file lives under the Skyrim (LE) domain, which has its own game id.
        await AddNexusItem(FakeMappingCache.SkyrimLe, 555, xxHash64: null, recordedSize: Size.FromLong(1000));
        var state = new NexusState("Skyrim", ModId: 9, FileId: 555, null, null, null, false);

        var have = await RowFor(Entry(state, size: 1000));
        have.State.Should().Be(ArchiveState.Have);

        var other = new NexusState("Skyrim", ModId: 9, FileId: 556, null, null, null, false);
        var browser = await RowFor(Entry(other, size: 1000));
        browser.State.Should().Be(ArchiveState.Browser);
        browser.Page.Should().Be(new Uri("https://www.nexusmods.com/skyrim/mods/9?tab=files&file_id=556&nmm=1"));
    }

    [Fact]
    public async Task ANexusFileIsAutomaticForAPremiumAccountAndBrowserForAFreeOne()
    {
        (await RowFor(Entry(SkyrimSeFile), premium: true)).State.Should().Be(ArchiveState.Automatic);

        var free = await RowFor(Entry(SkyrimSeFile), premium: false);
        free.State.Should().Be(ArchiveState.Browser);
        free.Page.Should().Be(new Uri("https://www.nexusmods.com/skyrimspecialedition/mods/77?tab=files&file_id=4242&nmm=1"));
    }

    [Fact]
    public async Task APlainHttpArchiveAndACdnArchiveAreAutomatic()
    {
        (await RowFor(Entry(new HttpState(new Uri("https://example.invalid/a.7z"), [])))).State.Should().Be(ArchiveState.Automatic);
        (await RowFor(Entry(new WabbajackCdnState(new Uri("https://authored-files.wabbajack.org/a.7z_guid"))))).State.Should().Be(ArchiveState.Automatic);
    }

    [Fact]
    public async Task AnHttpArchiveThatNeedsRequestHeadersIsManual()
    {
        var url = new Uri("https://example.invalid/a.7z");

        var row = await RowFor(Entry(new HttpState(url, ["Authorization: x"])));

        row.State.Should().Be(ArchiveState.Manual);
        row.Reason.Should().Be("needs request headers");
        row.Page.Should().Be(url);
    }

    [Fact]
    public async Task EveryManualSourceCarriesItsPage()
    {
        var url = new Uri("https://example.invalid/file");
        var cases = new (DownloadState State, Uri Page)[]
        {
            (new ManualState(url, "Click the big button"), url),
            (new MegaState(url), url),
            (new GoogleDriveState("abc123"), new Uri("https://drive.google.com/uc?id=abc123&export=download")),
            (new MediaFireState(url), url),
            (new ModDbState(url), url),
            (new LoversLabState(url, "a.7z"), url),
            (new VectorPlexusState(url, "a.7z"), url),
        };

        foreach (var (state, page) in cases)
        {
            var row = await RowFor(Entry(state));
            row.State.Should().Be(ArchiveState.Manual, state.GetType().Name);
            row.Page.Should().Be(page, state.GetType().Name);
        }
    }

    [Fact]
    public async Task AManualPromptIsKeptAsTheReason()
    {
        var row = await RowFor(Entry(new ManualState(new Uri("https://example.invalid/f"), "Click the big button")));

        row.Reason.Should().Be("Click the big button");
    }

    [Fact]
    public async Task AGameFileWhoseBytesMatchIsGameFile()
    {
        var state = new GameFileSourceState("SkyrimSpecialEdition", GameBsa, Hash64(GameBsaBytes), GameVersion);

        var row = await RowFor(Entry(state, Hash64(GameBsaBytes), GameBsaBytes.Length));

        row.State.Should().Be(ArchiveState.GameFile);
    }

    [Fact]
    public async Task AGameFileWithAChangedByteIsGameFileVersionAndNamesTheListsVersion()
    {
        var state = new GameFileSourceState("SkyrimSpecialEdition", GameBsa, Hash64(GameBsaBytes), "1.5.97");
        var changed = (byte[])GameBsaBytes.Clone();
        changed[10] ^= 0xFF;
        var loadout = await CreateLoadout();
        await GameBsaPath(loadout).WriteAllBytesAsync(changed);

        var plan = await Inventory().Build(ListWith(Entry(state, Hash64(GameBsaBytes), GameBsaBytes.Length)), Connection.Db, loadout);

        var row = plan.Rows.Single();
        row.State.Should().Be(ArchiveState.GameFileVersion);
        // The stub's executable carries no version resource, so the install's own version reads as unknown here.
        row.Reason.Should().Contain("1.5.97", "the version the list was made for").And.Contain("yours is");
    }

    [Fact]
    public async Task AGameFileIsStillGameFileWhenAnOriginalWasBackedUpAndTheLiveFileChanged()
    {
        var state = new GameFileSourceState("SkyrimSpecialEdition", GameBsa, Hash64(GameBsaBytes), GameVersion);
        var loadout = await CreateLoadout();
        await BackUpGameBsaAndChangeTheLiveFile(loadout);

        var plan = await Inventory().Build(ListWith(Entry(state, Hash64(GameBsaBytes), GameBsaBytes.Length)), Connection.Db, loadout);

        plan.Rows.Single().State.Should().Be(ArchiveState.GameFile, "the backup is read before the live file");
    }

    [Fact]
    public async Task AGameFileReadThroughTheInstallationAloneStillFindsItsBackedUpOriginal()
    {
        // A plan for a loadout the user has picked but not created yet only has the installation to go on.
        var state = new GameFileSourceState("SkyrimSpecialEdition", GameBsa, Hash64(GameBsaBytes), GameVersion);
        var loadout = await CreateLoadout();
        await BackUpGameBsaAndChangeTheLiveFile(loadout);
        var inventory = Inventory();

        var plan = await inventory.Build(ListWith(Entry(state, Hash64(GameBsaBytes), GameBsaBytes.Length)), Connection.Db, inventory.GameFilesFor(loadout.InstallationInstance));

        plan.Rows.Single().State.Should().Be(ArchiveState.GameFile, "the backups belong to the installation, not to a loadout");
    }

    // What a previous Apply leaves: the original in the store, pinned, and the live file replaced.
    private async Task BackUpGameBsaAndChangeTheLiveFile(Loadout.ReadOnly loadout)
    {
        var original = GameBsaBytes.xxHash3();
        await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(GameBsa, new MemoryStream(GameBsaBytes)), original, Size.FromLong(GameBsaBytes.Length))]);
        using (var tx = Connection.BeginTransaction())
        {
            _ = new GameBackedUpFile.New(tx) { Hash = original, GameInstallId = loadout.InstallationId };
            await tx.Commit();
        }
        var changed = (byte[])GameBsaBytes.Clone();
        changed[10] ^= 0xFF;
        await GameBsaPath(loadout).WriteAllBytesAsync(changed);
    }

    [Fact]
    public async Task AGameFileMissingFromTheGameFolderIsGameFileVersion()
    {
        var state = new GameFileSourceState("SkyrimSpecialEdition", RelativePath.FromUnsanitizedInput("Data/Absent.bsa"), Hash.From(1), GameVersion);

        var row = await RowFor(Entry(state, Hash.From(1)));

        row.State.Should().Be(ArchiveState.GameFileVersion);
        row.Reason.Should().Contain("Absent.bsa");
    }

    [Fact]
    public async Task AnUnknownStateIsUnsupportedWithItsTypeName()
    {
        var row = await RowFor(Entry(new UnsupportedState("BethesdaDownloader, Wabbajack.Lib", "https://bethesda.example/x")));

        row.State.Should().Be(ArchiveState.Unsupported);
        row.Reason.Should().Contain("BethesdaDownloader, Wabbajack.Lib");
    }

    private AbsolutePath GameBsaPath(Loadout.ReadOnly loadout)
        => loadout.InstallationInstance.Locations.ToAbsolutePath(new GamePath(LocationId.Game, GameBsa));
}
