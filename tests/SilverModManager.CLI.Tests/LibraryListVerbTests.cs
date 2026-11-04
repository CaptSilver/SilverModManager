using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.SingleProcess;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// <c>silvermod library list</c> and the dangling-item handling of <c>library export-lock</c>, against library rows
/// seeded in the test database. Every test lists through its own loadout (or its own game), so rows other tests left
/// in the shared database do not show up.
/// </summary>
public class LibraryListVerbTests(IServiceProvider provider) : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("silvermod-library-list-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private IConnection Connection => provider.GetRequiredService<IConnection>();

    private async Task<(int ExitCode, string Output)> Run(params string[] args)
    {
        var renderer = new LoggingRenderer();
        var exitCode = await provider.GetRequiredService<CommandLineConfigurator>().RunAsync(args, renderer, CancellationToken.None);
        return (exitCode, string.Join("\n", renderer.Logs.OfType<Text>().Select(t => t.Template)));
    }

    private async Task<Loadout.ReadOnly> CreateLoadout()
    {
        using var tx = Connection.BeginTransaction();
        var metadata = new GameInstallMetadata.New(tx)
        {
            Path = "/tmp/library-list-tests-" + Guid.NewGuid(),
            Name = "Library List Game",
            Store = GameStore.Unknown,
            GameId = NexusModsGameId.From(4333),
        };
        var loadout = new Loadout.New(tx)
        {
            Name = "Library List " + Guid.NewGuid(),
            ShortName = "LL",
            InstallationId = metadata,
            LoadoutKind = LoadoutKind.Default,
            Revision = 0,
            GameVersion = VanityVersion.From("Unknown"),
        };
        var result = await tx.Commit();
        return result.Remap(loadout);
    }

    private async Task Link(Loadout.ReadOnly loadout, LibraryFile.ReadOnly libraryItem, string groupName)
    {
        using var tx = Connection.BeginTransaction();
        var group = new LoadoutItemGroup.New(tx, out var id)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, id) { Name = groupName, LoadoutId = loadout.LoadoutId },
        };
        _ = new LibraryLinkedLoadoutItem.New(tx, id) { LibraryItemId = libraryItem.Id, LoadoutItemGroup = group };
        await tx.Commit();
    }

    private static string Unique(string stem) => $"{stem}-{Guid.NewGuid():N}";

    /// <summary>A library file with the given content; <paramref name="inStore"/> puts the bytes in the file store.</summary>
    private async Task<LibraryFile.ReadOnly> AddFile(string fileName, string content, Sha256Value? sha256, bool inStore, Action<ITransaction, LibraryFile.New>? extend = null)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = bytes.xxHash3();
        if (inStore)
            await provider.GetRequiredService<IFileStore>().BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(fileName, new MemoryStream(bytes)), FileIdentity.XxHash3(hash), Size.FromLong(bytes.Length))]);

        using var tx = Connection.BeginTransaction();
        var file = new LibraryFile.New(tx, out var id)
        {
            FileName = fileName,
            Hash = hash,
            Size = Size.From((ulong)bytes.Length),
            LibraryItem = new LibraryItem.New(tx, id) { Name = fileName },
        };
        if (sha256 is { } digest) tx.Add(id, LibraryFile.Sha256, digest);
        extend?.Invoke(tx, file);
        var result = await tx.Commit();
        return result.Remap(file);
    }

    private static Sha256Value DigestOf(string content) => Sha256Value.From(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private async Task<(LibraryFile.ReadOnly File, EntityId ModPage)> AddNexusFile(string name, uint modId, uint fileId, string version, bool inStore = true, string? thumbnail = "https://example.invalid/thumb.png", Sha256Value? sha256 = null, bool withSha256 = true, Action<ITransaction, LibraryFile.New>? alsoExtend = null)
    {
        EntityId modPage = default;
        var file = await AddFile(name + ".zip", name, withSha256 ? sha256 ?? DigestOf(name) : null, inStore, (tx, newFile) =>
        {
            alsoExtend?.Invoke(tx, newFile);
            var page = new NexusModsModPageMetadata.New(tx)
            {
                Uid = new ModUid(ModId.From(modId), NexusModsGameId.From(4333)),
                Name = name + " page",
                GameDomain = GameDomain.From("eldenring"),
                UpdatedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
                DataUpdatedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
            };
            if (thumbnail is not null) tx.Add(page.Id, NexusModsModPageMetadata.ThumbnailUri, new Uri(thumbnail));
            var metadata = new NexusModsFileMetadata.New(tx)
            {
                Uid = new FileUid(FileId.From(fileId), NexusModsGameId.From(4333)),
                Name = name,
                Version = version,
                UploadedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
                ModPageId = page,
            };
            tx.Add(newFile.Id, NexusModsLibraryItem.FileMetadataId, metadata.Id);
            tx.Add(newFile.Id, NexusModsLibraryItem.ModPageMetadataId, page.Id);
            modPage = page.Id;
        });
        return (file, modPage);
    }

    /// <summary>Adds a stored member to the archive the transaction is creating; the store never holds the archive itself.</summary>
    private async Task<(Action<ITransaction, LibraryFile.New> Extend, string MemberName)> ArchiveWithStoredMember(string memberContent)
    {
        var memberName = Unique("member") + ".esp";
        var bytes = Encoding.UTF8.GetBytes(memberContent);
        var hash = bytes.xxHash3();
        await provider.GetRequiredService<IFileStore>().BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(memberName, new MemoryStream(bytes)), FileIdentity.XxHash3(hash), Size.FromLong(bytes.Length))]);

        return (Extend, memberName);

        void Extend(ITransaction tx, LibraryFile.New archiveFile)
        {
            var archive = new LibraryArchive.New(tx, archiveFile.Id) { IsArchive = true, LibraryFile = archiveFile };
            var memberId = tx.TempId();
            _ = new LibraryArchiveFileEntry.New(tx, memberId)
            {
                Path = memberName,
                ParentId = archive.Id,
                LibraryFile = new LibraryFile.New(tx, memberId)
                {
                    FileName = memberName,
                    Hash = hash,
                    Size = Size.From((ulong)bytes.Length),
                    LibraryItem = new LibraryItem.New(tx, memberId) { Name = memberName },
                },
            };
        }
    }

    private static JsonObject Row(string output, string name)
        => JsonNode.Parse(output)!.AsArray().Select(n => n!.AsObject()).Single(row => (string?)row["name"] == name);

    [Fact]
    public async Task ANexusFileListsItsNexusIdsModPageAndThumbnail()
    {
        var loadout = await CreateLoadout();
        var (file, _) = await AddNexusFile(Unique("nexus"), modId: 77, fileId: 99, version: "2.5");
        await Link(loadout, file, "Nexus Mod");

        var (exitCode, output) = await Run("library", "list", "-l", loadout.Name, "--json");

        exitCode.Should().Be(0, output);
        var row = Row(output, file.FileName.ToString());
        ((string?)row["kind"]).Should().Be("nexus-mod-file");
        ((string?)row["id"]).Should().Be(file.AsLibraryItem().Id.ToString());
        ((long?)row["nexusGameId"]).Should().Be(4333);
        ((long?)row["nexusModId"]).Should().Be(77);
        ((long?)row["nexusFileId"]).Should().Be(99);
        ((string?)row["modPageId"]).Should().Be(NexusModsLibraryItem.Load(Connection.Db, file.Id).ModPageMetadata.Id.ToString());
        ((string?)row["modPageName"]).Should().EndWith(" page");
        ((string?)row["version"]).Should().Be("2.5");
        ((long?)row["size"]).Should().Be((long)file.Size.Value);
        ((bool?)row["inStore"]).Should().BeTrue();
        ((string?)row["thumbnailUri"]).Should().Be("https://example.invalid/thumb.png");
        ((bool?)row["dangling"]).Should().BeFalse();
        row["loadouts"]!.AsArray().Select(n => (string?)n).Should().Equal(loadout.Name);
    }

    [Fact]
    public async Task ANexusFileWithoutAThumbnailHasNullThumbnailUri()
    {
        var loadout = await CreateLoadout();
        var (file, _) = await AddNexusFile(Unique("nothumb"), 1, 2, "1", thumbnail: null);
        await Link(loadout, file, "No Thumb");

        var (_, output) = await Run("library", "list", "-l", loadout.Name, "--json");

        Row(output, file.FileName.ToString())["thumbnailUri"].Should().BeNull();
    }

    [Fact]
    public async Task ADownloadedFileListsItsHost()
    {
        var loadout = await CreateLoadout();
        var file = await AddFile(Unique("dl") + ".zip", "dl", DigestOf("dl"), inStore: true, (tx, newFile) =>
            _ = new DownloadedFile.New(tx, newFile.Id) { LibraryFile = newFile, DownloadPageUri = new Uri("https://files.example.invalid/some/page") });
        await Link(loadout, file, "Downloaded");

        var (_, output) = await Run("library", "list", "-l", loadout.Name, "--json");

        var row = Row(output, file.FileName.ToString());
        ((string?)row["kind"]).Should().Be("downloaded-file");
        ((string?)row["host"]).Should().Be("files.example.invalid");
        row["nexusModId"].Should().BeNull();
    }

    [Fact]
    public async Task ALocalFileIsListedAsLocal()
    {
        var loadout = await CreateLoadout();
        var file = await AddFile(Unique("local") + ".zip", "local", DigestOf("local"), inStore: true, (tx, newFile) =>
            _ = new LocalFile.New(tx, newFile.Id) { LibraryFile = newFile, OriginalPath = "/home/someone/local.zip" });
        await Link(loadout, file, "Local");

        var (_, output) = await Run("library", "list", "-l", loadout.Name, "--json");

        ((string?)Row(output, file.FileName.ToString())["kind"]).Should().Be("local-file");
    }

    [Fact]
    public async Task AWabbajackModlistIsListedWithItsVersion()
    {
        var loadout = await CreateLoadout();
        var file = await AddFile(Unique("list") + ".wabbajack", "list", DigestOf("list"), inStore: true, (tx, newFile) =>
            _ = new WabbajackModlistLibraryFile.New(tx, newFile.Id)
            {
                LibraryFile = newFile,
                Name = "A Modlist",
                Author = "someone",
                Version = "3.1.4",
                GameType = "Skyrim",
                ArchiveCount = 1,
                ArchiveSize = Size.From(1),
                InstallSize = Size.From(1),
                IsNsfw = false,
            });
        await Link(loadout, file, "Modlist");

        var (_, output) = await Run("library", "list", "-l", loadout.Name, "--json");

        var row = Row(output, file.FileName.ToString());
        ((string?)row["kind"]).Should().Be("wabbajack-modlist");
        ((string?)row["version"]).Should().Be("3.1.4");
    }

    [Fact]
    public async Task AnItemOfNoKnownKindIsListedAsOther()
    {
        var loadout = await CreateLoadout();
        var file = await AddFile(Unique("plain") + ".zip", "plain", DigestOf("plain"), inStore: true);
        await Link(loadout, file, "Plain");

        var (_, output) = await Run("library", "list", "-l", loadout.Name, "--json");

        ((string?)Row(output, file.FileName.ToString())["kind"]).Should().Be("other");
    }

    [Fact]
    public async Task ADanglingDownloadRecordIsFlaggedInJsonAndText()
    {
        var loadout = await CreateLoadout();
        var file = await AddFile(Guid.NewGuid() + ".tmp", "never stored", sha256: null, inStore: false, (tx, newFile) =>
            _ = new DownloadedFile.New(tx, newFile.Id) { LibraryFile = newFile, DownloadPageUri = new Uri("https://files.example.invalid/x") });
        await Link(loadout, file, "Dangling");

        var (_, json) = await Run("library", "list", "-l", loadout.Name, "--json");
        var (_, text) = await Run("library", "list", "-l", loadout.Name);

        var row = Row(json, file.FileName.ToString());
        ((bool?)row["dangling"]).Should().BeTrue();
        ((bool?)row["inStore"]).Should().BeFalse();
        text.Should().Contain(file.FileName.ToString()).And.Contain("dangling");
    }

    [Fact]
    public async Task TheLoadoutFilterListsOnlyItsItemsAndEachItemNamesEveryLoadoutThatLinksIt()
    {
        var first = await CreateLoadout();
        var second = await CreateLoadout();
        var shared = await AddFile(Unique("shared") + ".zip", "shared", DigestOf("shared"), inStore: true);
        var onlySecond = await AddFile(Unique("second") + ".zip", "second", DigestOf("second"), inStore: true);
        await Link(first, shared, "Shared");
        await Link(second, shared, "Shared");
        await Link(second, onlySecond, "Second");

        var (_, output) = await Run("library", "list", "-l", first.Name, "--json");

        var names = JsonNode.Parse(output)!.AsArray().Select(n => (string?)n!["name"]).ToArray();
        names.Should().Equal(shared.FileName.ToString());
        Row(output, shared.FileName.ToString())["loadouts"]!.AsArray().Select(n => (string?)n).Should().BeEquivalentTo([first.Name, second.Name]);
    }

    [Fact]
    public async Task TheGameFilterListsTheItemsDestinedForThatGame()
    {
        var game = GameId.From(Unique("list-game"));
        var other = GameId.From(Unique("other-game"));
        var mine = await AddFile(Unique("mine") + ".zip", "mine", DigestOf("mine"), inStore: true, (tx, newFile) =>
            tx.Add(newFile.Id, LibraryItem.DestinationGames, game));
        var theirs = await AddFile(Unique("theirs") + ".zip", "theirs", DigestOf("theirs"), inStore: true, (tx, newFile) =>
            tx.Add(newFile.Id, LibraryItem.DestinationGames, other));

        var (exitCode, output) = await Run("library", "list", "-g", game.ToString(), "--json");

        exitCode.Should().Be(0, output);
        var names = JsonNode.Parse(output)!.AsArray().Select(n => (string?)n!["name"]).ToArray();
        names.Should().Contain(mine.FileName.ToString()).And.NotContain(theirs.FileName.ToString());
    }

    [Fact]
    public async Task ListingChangesNothing()
    {
        var loadout = await CreateLoadout();
        var file = await AddFile(Unique("ro") + ".zip", "ro", sha256: null, inStore: true);
        await Link(loadout, file, "Read Only");

        await Run("library", "list", "-l", loadout.Name, "--json");

        LibraryFile.Load(Connection.Db, file.Id).Sha256.HasValue.Should().BeFalse("listing must not write the database, not even a back-filled digest");
    }

    [Fact]
    public async Task ExportLockSkipsADanglingItemAndSaysSo()
    {
        var loadout = await CreateLoadout();
        var dangling = await AddFile(Guid.NewGuid() + ".tmp", "never stored", sha256: null, inStore: false, (tx, newFile) =>
            _ = new DownloadedFile.New(tx, newFile.Id) { LibraryFile = newFile, DownloadPageUri = new Uri("https://files.example.invalid/x") });
        var (nexus, _) = await AddNexusFile(Unique("kept"), 5, 6, "1.0");
        await Link(loadout, dangling, "Dangling");
        await Link(loadout, nexus, "Kept");
        var output = Path.Combine(_root, "out.lock.toml");

        var (exitCode, text) = await Run("library", "export-lock", "--loadout", loadout.Name, "--out", output);

        exitCode.Should().Be(0, text);
        text.Should().Contain("[skipped]").And.Contain("Dangling").And.Contain("Wrote 1 entries");
        var written = await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken);
        written.Should().Contain(nexus.FileName.ToString()).And.NotContain(dangling.FileName.ToString());
    }

    [Fact]
    public async Task ExportLockJsonListsTheSkippedItems()
    {
        var loadout = await CreateLoadout();
        var dangling = await AddFile(Guid.NewGuid() + ".tmp", "never stored", sha256: null, inStore: false);
        await Link(loadout, dangling, "Dangling");

        var (exitCode, text) = await Run("library", "export-lock", "--loadout", loadout.Name, "--out", Path.Combine(_root, "out.lock.toml"), "--json");

        exitCode.Should().Be(0, text);
        var report = JsonNode.Parse(text)!.AsObject();
        report["entries"]!.AsArray().Should().BeEmpty();
        report["skipped"]!.AsArray().Select(n => (string?)n).Should().ContainSingle().Which.Should().Contain("Dangling");
    }

    [Fact]
    public async Task AnArchiveWhoseMembersAreStoredIsInStoreAndNotDanglingWithoutADigest()
    {
        var loadout = await CreateLoadout();
        var (extend, _) = await ArchiveWithStoredMember("member bytes");
        var (archive, _) = await AddNexusFile(Unique("archive"), 8, 9, "1.0", inStore: false, withSha256: false, alsoExtend: extend);
        await Link(loadout, archive, "Archive");

        var (_, output) = await Run("library", "list", "-l", loadout.Name, "--json");

        var row = Row(output, archive.FileName.ToString());
        ((bool?)row["inStore"]).Should().BeTrue("the archive's members are in the store");
        ((bool?)row["dangling"]).Should().BeFalse();
    }

    [Fact]
    public async Task ExportLockFailsOnAnArchiveWithoutADigestInsteadOfDroppingIt()
    {
        var loadout = await CreateLoadout();
        var (extend, _) = await ArchiveWithStoredMember("member bytes for the lock");
        var (archive, _) = await AddNexusFile(Unique("lockarchive"), 10, 11, "1.0", inStore: false, withSha256: false, alsoExtend: extend);
        await Link(loadout, archive, "Archive");

        var (exitCode, text) = await Run("library", "export-lock", "--loadout", loadout.Name, "--out", Path.Combine(_root, "archive.lock.toml"));

        exitCode.Should().NotBe(0, text);
        text.Should().Contain(archive.FileName.ToString()).And.NotContain("[skipped]");
    }

    [Fact]
    public async Task TheUnfilteredListLeavesOutArchiveMembers()
    {
        var (extend, memberName) = await ArchiveWithStoredMember("unfiltered member");
        var (archive, _) = await AddNexusFile(Unique("unfiltered"), 12, 13, "1.0", alsoExtend: extend);

        var (exitCode, output) = await Run("library", "list", "--json");

        exitCode.Should().Be(0, output);
        var names = JsonNode.Parse(output)!.AsArray().Select(n => (string?)n!["name"]).ToArray();
        names.Should().Contain(archive.FileName.ToString()).And.NotContain(memberName);
    }
}
