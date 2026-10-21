using System.Security.Cryptography;
using FluentAssertions;
using Xunit;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Backend;
using NexusMods.Library.Tests.DownloadsService;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Settings;

namespace NexusMods.Library.Tests;

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
