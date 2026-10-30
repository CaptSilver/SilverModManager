using FluentAssertions;
using SilverModManager.Sdk.Hashes;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusWebApi.Types;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Library;

namespace SilverModManager.Wabbajack.Tests.Acquire;

public class ArchiveInventoryTests(ITestOutputHelper helper) : AcquireTestBase<ArchiveInventoryTests>(helper)
{
    private ArchiveInventory Inventory(bool premium = false) => new(
        ServiceProvider.GetRequiredService<ILibraryArchiveLocator>(),
        Fakes.Login(premium),
        new FakeMappingCache(),
        Connection,
        FileStore,
        ServiceProvider.GetRequiredService<SilverModManager.Abstractions.Games.FileHashes.IFileIdentityResolver>(),
        ServiceProvider.GetRequiredService<IFileHashesService>());

    private async Task<ArchivePlanRow> RowFor(ArchiveEntry archive, bool premium = false)
    {
        var loadout = await CreateLoadout();
        var plan = await Inventory(premium).Build(ListWith(archive), Connection.Db, loadout);
        return plan.Rows.Should().ContainSingle().Subject;
    }

    private static readonly NexusState SkyrimSeFile = new("SkyrimSpecialEdition", ModId: 77, FileId: 4242, null, null, null, false);

    /// <summary>A library file the way a download from Nexus Mods leaves it, with the digests and size the test chooses.</summary>
    private async Task<LibraryFile.ReadOnly> AddNexusItem(NexusModsGameId game, uint fileId, FileIdentity? xxHash64, Size? recordedSize)
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
        if (xxHash64 is { } digest) tx.Add(id, LibraryFile.XxHash64, digest.XxHash64Value());

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

    private static ArchiveEntry Mo2Release(ulong id = 0x40) => new(Xx64.From(id), 85_879_000, "Mod.Organizer-2.4.4.7z", "", new HttpState(new Uri("https://github.com/ModOrganizer2/modorganizer/releases/download/v2.4.4/Mod.Organizer-2.4.4.7z"), []));

    private static FromArchive Taken(ArchiveEntry archive, string to)
        => new(RelativePath.FromUnsanitizedInput(to), Xx64.From(1), 1, new ArchiveHashPath(archive.XxHash64, [RelativePath.FromUnsanitizedInput("m")]));

    [Fact]
    public async Task ModOrganizerItselfIsNotNeededWhileTheRestOfTheListKeepsItsStates()
    {
        var mo2 = Mo2Release();
        var other = Entry(new HttpState(new Uri("https://example.invalid/a.zip"), []), Xx64.From(0x41));
        var list = ListWith(mo2, other) with
        {
            Directives = [Taken(mo2, "ModOrganizer.exe"), Taken(mo2, "plugins/x.py"), Taken(other, "mods/A/a.txt")],
        };
        var loadout = await CreateLoadout();

        var plan = await Inventory().Build(list, Connection.Db, loadout);

        plan.Rows.Select(r => r.State).Should().Equal(ArchiveState.NotNeeded, ArchiveState.Automatic);
        plan.Rows[0].Archive.Should().Be(mo2);
        plan.Rows[0].Page.Should().BeNull("nothing is fetched for it");
    }

    [Fact]
    public async Task AnArchiveOfModOrganizerTheLibraryAlreadyHoldsIsStillNotNeeded()
    {
        await using var zip = await CreateTestArchive(new Dictionary<RelativePath, byte[]> { [RelativePath.FromUnsanitizedInput("ModOrganizer.exe")] = "exe"u8.ToArray() });
        var library = await RegisterLocalArchive(zip.Path);
        var held = library.AsLibraryFile();
        var mo2 = new ArchiveEntry(held.XxHash64Identity()!.Value, (long)held.Size.Value, "Mod.Organizer-2.4.4.7z", "", new HttpState(new Uri("https://example.invalid/m.7z"), []));
        var loadout = await CreateLoadout();

        var plan = await Inventory().Build(ListWith(mo2) with { Directives = [Taken(mo2, "ModOrganizer.exe")] }, Connection.Db, loadout);

        plan.Rows.Should().ContainSingle().Which.State.Should().Be(ArchiveState.NotNeeded);
    }

    [Fact]
    public async Task ModOrganizerIsKeptWithANoteWhenTheListTakesAFileFromItForAMod()
    {
        var mo2 = Mo2Release();
        var list = ListWith(mo2) with { Directives = [Taken(mo2, "ModOrganizer.exe"), Taken(mo2, "mods/Tool/Data/x.esp")] };
        var loadout = await CreateLoadout();

        var plan = await Inventory().Build(list, Connection.Db, loadout);

        var row = plan.Rows.Should().ContainSingle().Subject;
        row.State.Should().Be(ArchiveState.Automatic);
        row.Note.Should().Contain("mods/Tool/Data/x.esp");
    }

    [Fact]
    public async Task AnArchiveAddedToTheLibraryIsHave()
    {
        await using var zip = await CreateTestArchive(new Dictionary<RelativePath, byte[]> { [RelativePath.FromUnsanitizedInput("a.txt")] = "hello"u8.ToArray() });
        var library = await RegisterLocalArchive(zip.Path);
        var hash = library.AsLibraryFile().XxHash64Identity()!.Value;

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
        await AddNexusItem(FakeMappingCache.SkyrimSe, 4242, xxHash64: Xx64.From(0x1111), recordedSize: Size.FromLong(1000));

        var row = await RowFor(Entry(SkyrimSeFile, hash: Xx64.From(0x2222), size: 1000));

        row.State.Should().Be(ArchiveState.Browser);
    }

    [Fact]
    public async Task ANexusFileWhoseRecordedXxHash64MatchesIsHave()
    {
        await AddNexusItem(FakeMappingCache.SkyrimSe, 4242, xxHash64: Xx64.From(0x2222), recordedSize: null);

        var row = await RowFor(Entry(SkyrimSeFile, hash: Xx64.From(0x2222), size: 1000));

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
    public async Task AnHttpArchiveWhoseHeadersAreSafeToSendIsAutomatic()
    {
        var url = new Uri("https://example.invalid/a.7z");

        var row = await RowFor(Entry(new HttpState(url, ["Referer: https://example.invalid/", "User-Agent: Wabbajack"])));

        row.State.Should().Be(ArchiveState.Automatic);
        row.Reason.Should().BeNull();
    }

    [Theory]
    [InlineData("Authorization: x", "Authorization")]
    [InlineData("Cookie: sid=1", "Cookie")]
    [InlineData("X-Api-Key: k", "X-Api-Key")]
    public async Task AnHttpArchiveThatNeedsAHeaderTheAppWillNotSendIsManualNamingIt(string header, string name)
    {
        var url = new Uri("https://example.invalid/a.7z");

        var row = await RowFor(Entry(new HttpState(url, ["Accept: */*", header])));

        row.State.Should().Be(ArchiveState.Manual);
        row.Reason.Should().Be($"the list asks for the request header {name}, which this app does not send");
        row.Page.Should().Be(url);
    }

    [Fact]
    public async Task EveryHostTheAppCanDownloadFromIsAutomaticAndCarriesNoReason()
    {
        var url = new Uri("https://example.invalid/file");
        var mega = new Uri("https://mega.nz/file/AbCdEf12#" + new string('A', 43));
        var states = new DownloadState[]
        {
            new MegaState(mega),
            new GoogleDriveState("abc123"),
            new MediaFireState(url),
            new ModDbState(url),
        };

        foreach (var state in states)
        {
            var row = await RowFor(Entry(state));
            row.State.Should().Be(ArchiveState.Automatic, state.GetType().Name);
            row.Reason.Should().BeNull(state.GetType().Name);
        }
    }

    [Theory]
    [InlineData("https://mega.nz/folder/AbCdEf12#abc")]
    [InlineData("https://mega.nz/file/AbCdEf12")]
    [InlineData("https://example.invalid/not-mega")]
    public async Task AMegaLinkThatIsNotAFileLinkIsManualWithItsPage(string address)
    {
        var url = new Uri(address);

        var row = await RowFor(Entry(new MegaState(url)));

        row.State.Should().Be(ArchiveState.Manual);
        row.Reason.Should().Be("the MEGA link is not a file link this app can read; download it in your browser");
        row.Page.Should().Be(url);
    }

    [Fact]
    public async Task ManualSourcesCarryTheirPageAndSayWhyTheAppCannotFetchThem()
    {
        var url = new Uri("https://example.invalid/file");
        var cases = new (DownloadState State, string? Reason)[]
        {
            (new ManualState(url, "Click the big button"), "Click the big button"),
            (new LoversLabState(url, "a.7z", 1, false), "Download 'a.7z' from the LoversLab page"),
            (new VectorPlexusState(url, "a.7z", 1, false), "Download 'a.7z' from the VectorPlexus page"),
        };

        foreach (var (state, reason) in cases)
        {
            var row = await RowFor(Entry(state));
            row.State.Should().Be(ArchiveState.Manual, state.GetType().Name);
            row.Page.Should().Be(url, state.GetType().Name);
            row.Reason.Should().Be(reason, state.GetType().Name);
        }
    }

    [Theory]
    [InlineData("LoversLab", true, null, "A forum attachment; download it from the page")]
    [InlineData("LoversLab", false, "x.7z", "Download 'x.7z' from the LoversLab page")]
    [InlineData("LoversLab", false, null, "Download the file from the LoversLab page")]
    [InlineData("VectorPlexus", true, null, "A forum attachment; download it from the page")]
    [InlineData("VectorPlexus", false, "x.7z", "Download 'x.7z' from the VectorPlexus page")]
    [InlineData("VectorPlexus", false, null, "Download the file from the VectorPlexus page")]
    public async Task AForumRowSaysWhichFileToFetchFromThePage(string site, bool attachment, string? fileName, string reason)
    {
        var url = new Uri("https://example.invalid/file");
        DownloadState state = site == "LoversLab"
            ? new LoversLabState(url, fileName, 1, attachment)
            : new VectorPlexusState(url, fileName, 1, attachment);

        var row = await RowFor(Entry(state));

        row.State.Should().Be(ArchiveState.Manual);
        row.Reason.Should().Be(reason);
        row.Page.Should().Be(url);
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

    private sealed class Reports : IProgress<PlanProgress>
    {
        public List<PlanProgress> All { get; } = [];
        public void Report(PlanProgress value) => All.Add(value);
    }

    [Fact]
    public async Task BuildReportsHowManyArchivesItHasCheckedAgainstTheLibrary()
    {
        var loadout = await CreateLoadout();
        var archives = Enumerable.Range(1, 3)
            .Select(i => Entry(new HttpState(new Uri($"https://example.invalid/{i}.zip"), []), Hash64([(byte)i]), 10, $"{i}.zip"))
            .ToArray();
        var reports = new Reports();

        await Inventory().Build(ListWith(archives), Connection.Db, Inventory().GameFilesFor(loadout), CancellationToken.None, reports);

        reports.All.Should().NotBeEmpty();
        reports.All.Should().OnlyContain(report => report.Phase == PlanPhase.CheckingLibrary);
        reports.All.Should().OnlyContain(report => report.Total == 3);
        reports.All.Last().Done.Should().Be(3, "the last report of a phase is never held back");
    }

    [Fact]
    public async Task BuildReportsTheBytesOfAGameFileItHashes()
    {
        var state = new GameFileSourceState("SkyrimSpecialEdition", GameBsa, Hash64(GameBsaBytes), GameVersion);
        var loadout = await CreateLoadout();
        var reports = new Reports();

        await Inventory().Build(ListWith(Entry(state, Hash64(GameBsaBytes), GameBsaBytes.Length)), Connection.Db, Inventory().GameFilesFor(loadout), CancellationToken.None, reports);

        var hashing = reports.All.Where(report => report.Phase == PlanPhase.HashingGameFiles).ToArray();
        hashing.Should().NotBeEmpty("a file the app has to read says how far it got");
        hashing.Should().OnlyContain(report => report.Total == GameBsaBytes.Length);
        hashing.Last().Done.Should().Be(GameBsaBytes.Length);
        hashing.Last().Item.Should().Contain(GameBsa.ToString());
        reports.All.Select(report => report.Phase).Should().BeInAscendingOrder("a phase never comes back once the next one began");
    }

    [Fact]
    public async Task BuildWithoutAProgressSinkStillPlans()
    {
        var loadout = await CreateLoadout();

        var plan = await Inventory().Build(ListWith(Entry(new HttpState(new Uri("https://example.invalid/a.zip"), []))), Connection.Db, Inventory().GameFilesFor(loadout), CancellationToken.None, null);

        plan.Rows.Should().ContainSingle();
    }

    // What a previous Apply leaves: the original in the store, pinned, and the live file replaced.
    private async Task BackUpGameBsaAndChangeTheLiveFile(Loadout.ReadOnly loadout)
    {
        var original = GameBsaBytes.xxHash3();
        await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(GameBsa, new MemoryStream(GameBsaBytes)), FileIdentity.XxHash3(original), Size.FromLong(GameBsaBytes.Length))]);
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
        var state = new GameFileSourceState("SkyrimSpecialEdition", RelativePath.FromUnsanitizedInput("Data/Absent.bsa"), Xx64.From(1), GameVersion);

        var row = await RowFor(Entry(state, Xx64.From(1)));

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
