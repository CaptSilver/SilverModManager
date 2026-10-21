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
