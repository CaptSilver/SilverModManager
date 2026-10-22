using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Backend;
using NexusMods.Library.Lock;
using NexusMods.Library.Tests.DownloadsService;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Library;
using NSubstitute;
using Xunit;

namespace NexusMods.Library.Tests.Lock;

public class LockVerifyTests(IConnection connection)
{
    private readonly LockFixtures _fixtures = new(connection);

    private static LockEntry Thunderstore(string version, string sha256) => new("zehs-repolib", version, null, $"Zehs-REPOLib-{version}.zip", sha256, LockSources.Thunderstore);

    [Fact]
    public async Task AThunderstoreEntryVerifiesByItsDigest()
    {
        var content = "repolib " + Guid.NewGuid();
        await _fixtures.AddThunderstoreFile("Zehs", "REPOLib", "4.2.0", content, LockFixtures.DigestOf(content));
        var verify = new LockVerify(connection, new LibraryFileDigests(Substitute.For<IFileStore>(), connection));

        var report = await verify.Verify(new LockFile([Thunderstore("4.2.0", LockFixtures.DigestOf(content).ToString())]), TestContext.Current.CancellationToken);

        report.Items.Should().ContainSingle().Which.Level.Should().Be(LockLevel.Ok);
    }

    [Fact]
    public async Task AThunderstoreEntryWhoseRowHasNoDigestVerifiesByTheZipFileNameFromTheStoredBytes()
    {
        // The digest is unique to this test, so the lookup by digest cannot find another test's row.
        var content = "old row repolib " + Guid.NewGuid();
        var file = await _fixtures.AddThunderstoreFile("Zehs", "REPOLib", "4.2.1", content, LockFixtures.DigestOf(content), storeDigest: false);
        var store = Substitute.For<IFileStore>();
        store.HaveFile(file.Hash).Returns(ValueTask.FromResult(true));
        store.GetFileStream(file.Hash, Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(content))));
        var verify = new LockVerify(connection, new LibraryFileDigests(store, connection));

        var report = await verify.Verify(new LockFile([Thunderstore("4.2.1", LockFixtures.DigestOf(content).ToString())]), TestContext.Current.CancellationToken);

        report.Items.Should().ContainSingle().Which.Level.Should().Be(LockLevel.Ok);
        await store.Received(1).GetFileStream(file.Hash, Arg.Any<CancellationToken>());
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
