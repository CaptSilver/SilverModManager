using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using FluentAssertions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Games;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Backend;
using SilverModManager.Library.ModIo;
using SilverModManager.Library.Tests.DownloadsService;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.ModIo;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;
using Xunit;

namespace SilverModManager.Library.Tests.ModIo;

public class AddModIoFileJobTests(IServiceProvider serviceProvider, IConnection connection)
{
    // The library keeps one file per set of bytes and the tests share a database, so each test gets bytes of its own.
    private readonly byte[] _payload = Guid.NewGuid().ToByteArray().Concat(Enumerable.Range(0, 3000).Select(i => (byte)(i * 7 + 3))).ToArray();
    private readonly string _fileName = $"mod-{Guid.NewGuid():N}.zip";

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

    private IJobTask<AddModIoFileJob, ModIoLibraryFile.ReadOnly> Run(ModIoFile file, Sha256Value? expected = null, PayloadHandler? handler = null)
        => AddModIoFileJob.Create(
            serviceProvider, new HttpClient(handler ?? new PayloadHandler(_payload)),
            gameId: 6715, modId: 4101, file, new Uri("https://mod.io/g/test/m/mod"), UnassignedAcquisition.Instance, dateUpdated: 1710000500, expected);

    private int LibraryCopies() => LibraryFile.All(connection.Db).Count(file => file.FileName == _fileName);

    [Fact]
    public async Task ADownloadGameVerifiesTheMd5AndAddsTheFileWithItsSha256()
    {
        var handler = new PayloadHandler(_payload);
        var listed = TestMods.File(88001, _fileName, _payload, version: "1.2.0");

        var result = await Run(listed, handler: handler);

        handler.Requests.Distinct().Should().Equal(new Uri(listed.Download!.BinaryUrl));
        (result.GameId, result.ModId, result.FileId).Should().Be((6715u, 4101u, 88001u));
        result.Version.Should().Be("1.2.0");
        result.DateUpdated.Should().Be(1710000500);
        var file = result.AsLibraryFile();
        file.FileName.Should().Be(_fileName);
        file.Md5.Value.ToString().Should().BeEquivalentTo(Convert.ToHexStringLower(MD5.HashData(_payload)));
        file.Sha256.Value.Should().Be(PayloadSha);
    }

    [Fact]
    public async Task AMismatchedMd5AddsNothing()
    {
        var act = async () => await Run(TestMods.File(1, _fileName, _payload, md5: new string('b', 32)));

        await act.Should().ThrowAsync<ModIoIntegrityException>().WithMessage("*MD5 mismatch*");
        LibraryCopies().Should().Be(0);
    }

    [Fact]
    public async Task APinnedDigestThatDoesNotMatchAddsNothing()
    {
        var wrong = Sha256Value.From(SHA256.HashData("other"u8));

        var act = async () => await Run(TestMods.File(1, _fileName, _payload), wrong);

        await act.Should().ThrowAsync<ModIoIntegrityException>().WithMessage("*SHA-256 mismatch*");
        LibraryCopies().Should().Be(0);
    }

    [Fact]
    public async Task AFileWithNeitherAChecksumNorAPinIsRefusedBeforeTheDownload()
    {
        var handler = new PayloadHandler(_payload);
        var listed = TestMods.File(1, _fileName, _payload) with { FileHash = null };

        var act = async () => await Run(listed, handler: handler);

        await act.Should().ThrowAsync<ModIoIntegrityException>().WithMessage("*no usable checksum*");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task APinLetsAFileWithoutAChecksumThrough()
    {
        var listed = TestMods.File(1, _fileName, _payload) with { FileHash = null };

        var result = await Run(listed, PayloadSha);

        result.AsLibraryFile().Sha256.Value.Should().Be(PayloadSha);
    }

    [Fact]
    public async Task AFileNameThatCouldNamePathIsRefused()
    {
        var act = async () => await Run(TestMods.File(1, "../evil.zip", _payload));

        await act.Should().ThrowAsync<ModIoIntegrityException>().WithMessage("*not a plain file name*");
    }

    [Fact]
    public async Task AFileWithoutADownloadAddressIsRefused()
    {
        var act = async () => await Run(TestMods.File(1, _fileName, _payload) with { Download = null });

        await act.Should().ThrowAsync<ModIoIntegrityException>().WithMessage("*no download address*");
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
