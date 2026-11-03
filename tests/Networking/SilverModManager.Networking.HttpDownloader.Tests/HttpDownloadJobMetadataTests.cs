using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;

namespace SilverModManager.Networking.HttpDownloader.Tests;

/// <summary>
/// A plain download has to leave a model behind in the library; a library file with no model is listed by no page.
/// </summary>
public class HttpDownloadJobMetadataTests(IServiceProvider serviceProvider)
{
    [Fact]
    public async Task AddMetadata_RecordsTheDownloadPageOnADownloadedFile()
    {
        var connection = serviceProvider.GetRequiredService<IConnection>();
        var page = new Uri("https://www.example.com/files/archive");
        var job = new HttpDownloadJob
        {
            Client = new HttpClient(),
            Logger = NullLogger.Instance,
            Uri = new Uri("https://cdn.example.com/archive.7z?signature=SECRET"),
            DownloadPageUri = page,
            Destination = serviceProvider.GetRequiredService<TemporaryFileManager>().CreateFile().Path,
        };

        using var tx = connection.BeginTransaction();
        var bytes = System.Text.Encoding.UTF8.GetBytes(Guid.NewGuid().ToString());
        var file = new LibraryFile.New(tx, out var id)
        {
            FileName = "archive.7z",
            Hash = bytes.xxHash3(),
            Size = Size.From((ulong)bytes.Length),
            LibraryItem = new LibraryItem.New(tx, id) { Name = "archive.7z" },
        };
        await job.AddMetadata(tx, file);
        var result = await tx.Commit();

        var downloaded = DownloadedFile.Load(connection.Db, result.Remap(file).Id);
        downloaded.IsValid().Should().BeTrue();
        downloaded.DownloadPageUri.Should().Be(page);
    }
}
