using SilverModManager.Library.GameBanana;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Backend;
using NexusMods.Paths;
using SilverModManager.Library.Tests.DownloadsService;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.GameBanana.DTOs;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;
using Xunit;

namespace SilverModManager.Library.Tests.GameBanana;

public class AddGameBananaFileJobTests(IServiceProvider serviceProvider, IConnection connection, TemporaryFileManager temporaryFileManager)
{
    // The library keeps one file per set of bytes and the tests share a database, so each test gets bytes of its own.
    private readonly byte[] _payload = Guid.NewGuid().ToByteArray().Concat(Enumerable.Range(0, 3000).Select(i => (byte)(i * 7 + 3))).ToArray();
    private readonly string _fileName = $"mod-{Guid.NewGuid():N}.zip";

    private string PayloadMd5 => Convert.ToHexStringLower(MD5.HashData(_payload));
    private Sha256Value PayloadSha => Sha256Value.From(SHA256.HashData(_payload));

    private sealed class PayloadHandler(byte[] payload) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            return Task.FromResult(response);
        }
    }

    private GameBananaFile FileListed(string? md5, string? name = null)
        => new(583960, name ?? _fileName, _payload.Length, "https://gamebanana.com/dl/583960", md5, "clean");

    private IJobTask<AddGameBananaFileJob, GameBananaLibraryFile.ReadOnly> Run(GameBananaFile file, Sha256Value? expected = null, PayloadHandler? handler = null)
        => AddGameBananaFileJob.Create(
            serviceProvider, new HttpClient(handler ?? new PayloadHandler(_payload)),
            modId: 44334, file, new Uri("https://gamebanana.com/mods/44334"), UnassignedAcquisition.Instance, gameId: 297, dateUpdated: 1622678310, version: "21.7", expected);

    private int LibraryCopies() => LibraryFile.All(connection.Db).Count(file => file.FileName == _fileName);

    private async Task<int> TemporaryEntryCount()
    {
        var probe = temporaryFileManager.CreateFolder();
        var root = probe.Path.Parent.ToString();
        await probe.DisposeAsync();
        return Directory.GetFileSystemEntries(root).Length;
    }

    [Fact]
    public async Task AMatchingMd5AddsTheFileWithItsSha256()
    {
        var handler = new PayloadHandler(_payload);

        var result = await Run(FileListed(PayloadMd5), handler: handler);

        handler.Requests.Distinct().Should().Equal(new Uri("https://gamebanana.com/dl/583960"));
        result.ModId.Should().Be(44334);
        result.FileId.Should().Be(583960);
        result.GameId.Should().Be(297);
        result.DateUpdated.Should().Be(1622678310);
        result.ListedFileName.Should().Be(_fileName);
        result.ListedVersion.Value.Should().Be("21.7");
        var file = result.AsLibraryFile();
        file.FileName.Should().Be(_fileName);
        file.Md5.Value.ToString().Should().BeEquivalentTo(PayloadMd5);
        file.Sha256.Value.Should().Be(PayloadSha);
    }

    [Fact]
    public async Task AMismatchedMd5AddsNothing()
    {
        var before = await TemporaryEntryCount();
        var copiesBefore = LibraryCopies();

        var act = async () => await Run(FileListed(new string('0', 32)));

        var failure = await act.Should().ThrowAsync<GameBananaIntegrityException>();
        failure.Which.Message.Should().Contain(new string('0', 32)).And.Contain(PayloadMd5);
        LibraryCopies().Should().Be(copiesBefore);
        (await TemporaryEntryCount()).Should().Be(before);
    }

    [Fact]
    public async Task APinnedSha256ThatDoesNotMatchAddsNothing()
    {
        var copiesBefore = LibraryCopies();
        var wrongPin = Sha256Value.From(SHA256.HashData("other"u8));

        var act = async () => await Run(FileListed(PayloadMd5), wrongPin);

        await act.Should().ThrowAsync<GameBananaIntegrityException>();
        LibraryCopies().Should().Be(copiesBefore);
    }

    [Fact]
    public async Task AMatchingPinNeedsNoListedMd5()
    {
        // A lock names the file id and a digest but not the page's checksum.
        var result = await Run(FileListed(md5: null), PayloadSha);

        result.AsLibraryFile().Sha256.Value.Should().Be(PayloadSha);
    }

    [Fact]
    public async Task AFileWithNeitherAChecksumNorAPinIsRefusedBeforeDownloading()
    {
        var handler = new PayloadHandler(_payload);

        var act = async () => await Run(FileListed(md5: null), handler: handler);

        await act.Should().ThrowAsync<GameBananaIntegrityException>();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ANameThatIsAPathIsRefused()
    {
        var handler = new PayloadHandler(_payload);

        var act = async () => await Run(FileListed(PayloadMd5, "../evil.zip"), handler: handler);

        await act.Should().ThrowAsync<GameBananaIntegrityException>();
        handler.Requests.Should().BeEmpty();
    }

    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            DIHelpers.ConfigureServices(services);
            services.AddLibraryModels().AddOSInterop().AddFileExtractors();
        }
    }
}
