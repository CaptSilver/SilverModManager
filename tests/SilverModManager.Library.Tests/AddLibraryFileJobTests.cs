using System.IO.Compression;
using System.Security.Cryptography;
using FluentAssertions;
using Xunit;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Backend;
using SilverModManager.Library.Tests.DownloadsService;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Library.Tests;

public class AddLibraryFileJobTests(IServiceProvider serviceProvider, IConnection connection, TemporaryFileManager temporaryFileManager)
{
    // 7z-with-symlinks.7z holds links aimed at an absolute path and at ../outside-secret.txt next to the
    // extraction folder, plus links to files and a folder inside the archive. The secret is unreadable, so
    // reading through a link that escaped would fail the import instead of quietly hashing it.
    [Fact]
    public async Task ImportingAnArchiveWithSymlinksNeverReadsOutsideTheArchive()
    {
        // The extraction folder is created next to this one, so ../ from its root lands here.
        await using var sibling = temporaryFileManager.CreateFolder();
        var secret = sibling.Path.Parent.Combine("outside-secret.txt");
        await secret.WriteAllTextAsync("private", TestContext.Current.CancellationToken);
        File.SetUnixFileMode(secret.ToString(), UnixFileMode.None);

        try
        {
            var archive = FileSystem.Shared.GetKnownPath(KnownPath.CurrentDirectory).Combine("Resources/7z-with-symlinks.7z");

            using var tx = connection.BeginTransaction();
            var libraryFile = await AddLibraryFileJob.Create(serviceProvider, tx, archive);
            var result = await tx.Commit();

            result.Remap(libraryFile).TryGetAsLibraryArchive(out var libraryArchive).Should().BeTrue();
            libraryArchive.Children.Select(c => c.Path.ToString()).Should().BeEquivalentTo(
                "real.txt", "sub/inner.txt", "link-file", "link-chain", "link-sub-file"
            );
        }
        finally
        {
            File.SetUnixFileMode(secret.ToString(), UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Delete(secret.ToString());
        }
    }

    [Fact]
    public async Task AddingAFileRecordsItsSha256()
    {
        await using var folder = temporaryFileManager.CreateFolder();
        var file = folder.Path.Combine("plain.bin");
        var bytes = Enumerable.Range(0, 5000).Select(i => (byte)(i * 31)).ToArray();
        await File.WriteAllBytesAsync(file.ToString(), bytes, TestContext.Current.CancellationToken);

        using var tx = connection.BeginTransaction();
        var libraryFile = await AddLibraryFileJob.Create(serviceProvider, tx, file);
        var result = await tx.Commit();

        var stored = result.Remap(libraryFile);
        stored.Sha256.HasValue.Should().BeTrue();
        stored.Sha256.Value.ToString().Should().Be(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    [Fact]
    public async Task NestedArchiveEntriesHaveNoSha256()
    {
        var archive = FileSystem.Shared.GetKnownPath(KnownPath.CurrentDirectory).Combine("Resources/7z-with-symlinks.7z");

        using var tx = connection.BeginTransaction();
        var libraryFile = await AddLibraryFileJob.Create(serviceProvider, tx, archive);
        var result = await tx.Commit();

        var stored = result.Remap(libraryFile);
        stored.Sha256.HasValue.Should().BeTrue("the archive itself is a top-level file");
        stored.TryGetAsLibraryArchive(out var libraryArchive).Should().BeTrue();
        libraryArchive.Children.Should().NotBeEmpty();
        libraryArchive.Children.Should().OnlyContain(child => !child.AsLibraryFile().Sha256.HasValue);
    }

    [Fact]
    public async Task AddingAFileRecordsItsXxHash64()
    {
        await using var folder = temporaryFileManager.CreateFolder();
        var file = folder.Path.Combine("plain.bin");
        var bytes = Enumerable.Range(0, 5000).Select(i => (byte)(i * 17)).ToArray();
        await File.WriteAllBytesAsync(file.ToString(), bytes, TestContext.Current.CancellationToken);

        using var tx = connection.BeginTransaction();
        var libraryFile = await AddLibraryFileJob.Create(serviceProvider, tx, file);
        var result = await tx.Commit();

        var stored = result.Remap(libraryFile);
        stored.XxHash64.HasValue.Should().BeTrue();
        stored.XxHash64.Value.Should().Be(Xx64Hasher.Hash(bytes));
    }

    [Fact]
    public async Task AddingAZipRecordsXxHash64OnTheArchiveAndEveryMember()
    {
        await using var folder = temporaryFileManager.CreateFolder();
        var zipPath = folder.Path.Combine("pack.zip");
        var members = new Dictionary<string, byte[]>
        {
            ["a.txt"] = "first member"u8.ToArray(),
            ["sub/b.bin"] = Enumerable.Range(0, 3000).Select(i => (byte)(i * 5)).ToArray(),
        };
        using (var zip = new ZipArchive(File.Create(zipPath.ToString()), ZipArchiveMode.Create))
        {
            foreach (var (name, bytes) in members)
            {
                using var entry = zip.CreateEntry(name).Open();
                entry.Write(bytes);
            }
        }

        using var tx = connection.BeginTransaction();
        var libraryFile = await AddLibraryFileJob.Create(serviceProvider, tx, zipPath);
        var result = await tx.Commit();

        var stored = result.Remap(libraryFile);
        await using (var stream = zipPath.Read())
            stored.XxHash64.Value.Should().Be(await Xx64Hasher.HashAsync(stream, cancellationToken: TestContext.Current.CancellationToken));

        stored.TryGetAsLibraryArchive(out var libraryArchive).Should().BeTrue();
        libraryArchive.Children.Should().HaveCount(members.Count);
        foreach (var child in libraryArchive.Children)
        {
            var file = child.AsLibraryFile();
            file.XxHash64.HasValue.Should().BeTrue();
            file.XxHash64.Value.Should().Be(Xx64Hasher.Hash(members[child.Path.ToString()]));
        }
    }

    // The inner zip stores "filename_with_" + 0x8B + "o" + 0x94 + ".txt" as raw code page 437 bytes (no UTF-8 flag).
    [Fact]
    public async Task AnArchiveHoldingAZipWithLegacyEncodedNamesCanBeAdded()
    {
        var archive = FileSystem.Shared.GetKnownPath(KnownPath.CurrentDirectory).Combine("Resources/7z-with-cp437-zip.7z");

        using var tx = connection.BeginTransaction();
        var libraryFile = await AddLibraryFileJob.Create(serviceProvider, tx, archive);
        var result = await tx.Commit();

        result.Remap(libraryFile).TryGetAsLibraryArchive(out var outer).Should().BeTrue();
        var inner = outer.Children.Should().ContainSingle().Which;
        inner.Path.ToString().Should().Be("legacy-names.zip");
        inner.AsLibraryFile().TryGetAsLibraryArchive(out var innerArchive).Should().BeTrue();
        innerArchive.Children.Select(c => c.Path.ToString()).Should().Equal("filename_with_\u00EFo\u00F6.txt");
    }

    [Fact]
    public async Task ANestedArchiveThatCannotBeOpenedIsKeptAsAPlainFile()
    {
        await using var folder = temporaryFileManager.CreateFolder();
        var outerPath = folder.Path.Combine("outer.zip");
        // Starts like a zip so the signature check accepts it, but is cut off after the first bytes.
        byte[] broken = [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x00, 0x00, 0x08, 0x00, 0xDE, 0xAD, 0xBE, 0xEF];
        using (var zip = new ZipArchive(File.Create(outerPath.ToString()), ZipArchiveMode.Create))
        {
            using (var entry = zip.CreateEntry("broken.zip").Open()) entry.Write(broken);
            using (var entry = zip.CreateEntry("fine.txt").Open()) entry.Write("fine"u8);
        }

        using var tx = connection.BeginTransaction();
        var libraryFile = await AddLibraryFileJob.Create(serviceProvider, tx, outerPath);
        var result = await tx.Commit();

        var stored = result.Remap(libraryFile);
        stored.Sha256.Value.ToString().Should().Be(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(outerPath.ToString(), TestContext.Current.CancellationToken))).ToLowerInvariant());
        stored.TryGetAsLibraryArchive(out var outer).Should().BeTrue();
        outer.Children.Select(c => c.Path.ToString()).Should().BeEquivalentTo("broken.zip", "fine.txt");

        var brokenEntry = outer.Children.Single(c => c.Path.ToString() == "broken.zip").AsLibraryFile();
        brokenEntry.TryGetAsLibraryArchive(out _).Should().BeFalse("an archive that can't be opened is recorded as the file it is");
        brokenEntry.Size.Value.Should().Be((ulong)broken.Length);
    }

    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            DIHelpers.ConfigureServices(services);
            services
                .AddLibraryModels()
                .AddOSInterop()
                .AddFileExtractors();
        }
    }
}
