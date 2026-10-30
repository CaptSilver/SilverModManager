using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Library;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.DataModel.SchemaVersions.Tests;

/// <summary>
/// Modlists added before their archives were recorded get the archive rows, and the files already in the library
/// that a list names are marked as fetched for it.
/// </summary>
public class ModlistArchiveLinkMigrationTests
{
    private static readonly byte[] Held = "an archive the library already holds"u8.ToArray();
    private static readonly byte[] Missing = "an archive the library lacks"u8.ToArray();

    private static string ListJson(string name, params (byte[] Content, string Name)[] archives)
    {
        var entries = string.Join(",", archives.Select(a =>
            $$$"""{"$type":"Archive, Wabbajack.Lib","Hash":"{{{WabbajackHash.ToBase64(FileIdentity.XxHash64(Xx64Hasher.Hash(a.Content)))}}}","Size":{{{a.Content.Length}}},"Name":"{{{a.Name}}}","Meta":"","State":{"$type":"HttpDownloader, Wabbajack.Lib","Url":"https://example.invalid/{{{a.Name}}}","Headers":[]}}"""));
        return $$$"""{"$type":"ModList, Wabbajack.Lib","Name":"{{{name}}}","Author":"","Description":"","Version":"1.0","GameType":"SkyrimSpecialEdition","OtherGames":[],"Image":"","Readme":"","Website":"","Community":"","IsNSFW":false,"Archives":[{{{entries}}}],"Directives":[]}""";
    }

    private static async Task<LibraryFileId> AddList(ILibraryService library, OnDiskDataFolder folder, string name, params (byte[] Content, string Name)[] archives)
    {
        var path = folder.DatabaseFolder.Parent.Combine($"{name}.wabbajack");
        path.Parent.CreateDirectory();
        using (var zip = new ZipArchive(File.Create(path.ToString()), ZipArchiveMode.Create))
        {
            using var entry = zip.CreateEntry("modlist").Open();
            entry.Write(Encoding.UTF8.GetBytes(ListJson(name, archives)));
        }
        return LibraryFileId.From((await library.AddLocalFile(path)).AsLibraryFile().Id);
    }

    private static async Task Forget(IConnection connection, LibraryFileId list)
    {
        using var tx = connection.BeginTransaction();
        foreach (var row in WabbajackModlistArchive.FindByModlist(connection.Db, list.Value)) tx.Delete(row.Id, recursive: false);
        await tx.Commit();
    }

    private static async Task AddFile(ILibraryService library, OnDiskDataFolder folder, string name, byte[] content)
    {
        var path = folder.DatabaseFolder.Parent.Combine(name);
        path.Parent.CreateDirectory();
        await path.WriteAllBytesAsync(content);
        await library.AddLocalFile(path);
    }

    [Fact]
    public async Task AListWithoutRowsGainsThemAndAHeldFileIsMarkedFetched()
    {
        using var folder = new OnDiskDataFolder();
        await using var provider = folder.CreateProvider();
        var connection = provider.GetRequiredService<IConnection>();
        var library = provider.GetRequiredService<ILibraryService>();
        await AddFile(library, folder, "held.txt", Held);
        var list = await AddList(library, folder, "legacy", (Held, "held.7z"), (Missing, "missing.7z"));
        await Forget(connection, list);
        WabbajackModlistArchive.FindByModlist(connection.Db, list.Value).Should().BeEmpty();

        await provider.GetRequiredService<MigrationService>().MigrateAll();

        var rows = WabbajackModlistArchive.FindByModlist(connection.Db, list.Value).ToDictionary(r => r.Name);
        rows.Keys.Should().BeEquivalentTo(["held.7z", "missing.7z"]);
        rows["held.7z"].FetchedFiles.Should().ContainSingle();
        rows["missing.7z"].FetchedFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task AListThatAlreadyHasRowsIsLeftAlone()
    {
        using var folder = new OnDiskDataFolder();
        await using var provider = folder.CreateProvider();
        var connection = provider.GetRequiredService<IConnection>();
        var library = provider.GetRequiredService<ILibraryService>();
        var list = await AddList(library, folder, "current", (Held, "held.7z"));
        var before = WabbajackModlistArchive.FindByModlist(connection.Db, list.Value).Select(r => r.Id).ToArray();
        await AddFile(library, folder, "held.txt", Held);

        await provider.GetRequiredService<MigrationService>().MigrateAll();

        var rows = WabbajackModlistArchive.FindByModlist(connection.Db, list.Value).ToArray();
        rows.Select(r => r.Id).Should().BeEquivalentTo(before);
        rows.Single().FetchedFiles.Should().BeEmpty("a file the user added after the list is the user's");
    }

    [Fact]
    public async Task AListThatCannotBeReadIsSkippedAndTheOthersStillMigrate()
    {
        using var folder = new OnDiskDataFolder();
        await using var provider = folder.CreateProvider();
        var connection = provider.GetRequiredService<IConnection>();
        var library = provider.GetRequiredService<ILibraryService>();
        // A plain file marked as a modlist: it has no 'modlist' entry to read.
        var plain = folder.DatabaseFolder.Parent.Combine("broken.wabbajack");
        plain.Parent.CreateDirectory();
        await plain.WriteAllBytesAsync("not a zip"u8.ToArray());
        var broken = LibraryFileId.From((await library.AddLocalFile(plain)).AsLibraryFile().Id);
        using (var tx = connection.BeginTransaction())
        {
            tx.Add(broken.Value, WabbajackModlistLibraryFile.Name, "broken");
            tx.Add(broken.Value, WabbajackModlistLibraryFile.Author, "");
            tx.Add(broken.Value, WabbajackModlistLibraryFile.Version, "1");
            tx.Add(broken.Value, WabbajackModlistLibraryFile.GameType, "SkyrimSpecialEdition");
            tx.Add(broken.Value, WabbajackModlistLibraryFile.ArchiveCount, 3);
            tx.Add(broken.Value, WabbajackModlistLibraryFile.ArchiveSize, Size.From(3));
            tx.Add(broken.Value, WabbajackModlistLibraryFile.InstallSize, Size.From(0));
            tx.Add(broken.Value, WabbajackModlistLibraryFile.IsNsfw, false);
            await tx.Commit();
        }
        var good = await AddList(library, folder, "good", (Missing, "missing.7z"));
        await Forget(connection, good);

        await provider.GetRequiredService<MigrationService>().MigrateAll();

        WabbajackModlistArchive.FindByModlist(connection.Db, broken.Value).Should().BeEmpty();
        WabbajackModlistArchive.FindByModlist(connection.Db, good.Value).Should().ContainSingle();
    }
}
