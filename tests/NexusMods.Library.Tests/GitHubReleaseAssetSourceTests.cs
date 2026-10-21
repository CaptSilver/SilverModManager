using FluentAssertions;
using NexusMods.Abstractions.Library;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Networking.GitHub.DTOs;
using NexusMods.Sdk.Hashes;
using NexusMods.Sdk.Library;
using Xunit;

namespace NexusMods.Library.Tests;

// Part of the job's test class: its nested Startup is what builds the service graph these tests need.
public partial class AddGitHubReleaseAssetJobTests
{
    private const string LinuxName = "me3-linux-amd64.tar.gz";

    private static Asset Me3Asset(string name, string? digest) => new(
        Url: "u", Id: 1, Name: name, ContentType: "application/octet-stream", State: "uploaded", Size: Payload.Length,
        DownloadCount: 0, CreatedAt: default, UpdatedAt: default, BrowserDownloadUrl: "https://example.invalid/" + name, Digest: digest
    );

    private static Release Me3Release(params Asset[] assets) => new(
        "u", "u", "https://github.com/o/r/releases/tag/v0.13.0", 1, "v0.13.0", "main", "v0.13.0", false, false, default, default, assets, "u", "u", ""
    );

    private GitHubReleaseAssetSource SourceFor(Release? release)
        => new(serviceProvider, new StubApi(release), new HttpClient(new PayloadHandler(Payload)));

    private int LibraryCopies(string name) => LibraryFile.All(connection.Db).Count(file => file.FileName == name);

    [Fact]
    public async Task TheLinuxTarballIsPickedOverTheWindowsZipAndTheDebugBuilds()
    {
        var release = Me3Release(
            Me3Asset("me3-windows-amd64.zip", Digest(OtherDigest)),
            Me3Asset("me3-windows-amd64-debug.zip", Digest(OtherDigest)),
            Me3Asset("me3-linux-amd64-debug.tar.gz", Digest(OtherDigest)),
            Me3Asset(LinuxName, Digest(PayloadDigest))
        );

        var file = await SourceFor(release).AddLatestAssetAsync(540883721, "linux-amd64.tar.gz", "me3-linux-amd64", CancellationToken.None);

        file.FileName.Should().Be(LinuxName);
        file.Sha256.Value.Should().Be(PayloadDigest);
    }

    [Fact]
    public async Task AReleaseWithOnlyTheWindowsZipAndDebugBuildsFailsInsteadOfTakingOne()
    {
        var release = Me3Release(Me3Asset("me3-windows-amd64.zip", Digest(PayloadDigest)), Me3Asset("me3-linux-amd64-debug.tar.gz", Digest(PayloadDigest)));
        var before = LibraryCopies("me3-windows-amd64.zip") + LibraryCopies("me3-linux-amd64-debug.tar.gz");

        var act = async () => await SourceFor(release).AddLatestAssetAsync(540883721, "linux-amd64.tar.gz", "me3-linux-amd64", CancellationToken.None);

        await act.Should().ThrowAsync<ReleaseAssetIntegrityException>();
        (LibraryCopies("me3-windows-amd64.zip") + LibraryCopies("me3-linux-amd64-debug.tar.gz")).Should().Be(before);
    }

    [Fact]
    public async Task ADigestThatDoesNotMatchTheBytesAddsNothing()
    {
        var release = Me3Release(Me3Asset("mismatch-linux-amd64.tar.gz", Digest(OtherDigest)));

        var act = async () => await SourceFor(release).AddLatestAssetAsync(540883721, "linux-amd64.tar.gz", null, CancellationToken.None);

        await act.Should().ThrowAsync<ReleaseAssetIntegrityException>().WithMessage("*mismatch-linux-amd64.tar.gz*");
        LibraryCopies("mismatch-linux-amd64.tar.gz").Should().Be(0);
    }

    [Fact]
    public async Task AnAssetWithoutADigestIsRefusedBecauseNothingCouldVerifyIt()
    {
        var release = Me3Release(Me3Asset("undigested-linux-amd64.tar.gz", digest: null));

        var act = async () => await SourceFor(release).AddLatestAssetAsync(540883721, "linux-amd64.tar.gz", null, CancellationToken.None);

        await act.Should().ThrowAsync<ReleaseAssetIntegrityException>().WithMessage("*no SHA-256*");
        LibraryCopies("undigested-linux-amd64.tar.gz").Should().Be(0);
    }

    [Fact]
    public async Task ACancelledRequestDownloadsAndAddsNothing()
    {
        var release = Me3Release(Me3Asset("cancelled-linux-amd64.tar.gz", Digest(PayloadDigest)));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = async () => await SourceFor(release).AddLatestAssetAsync(540883721, "linux-amd64.tar.gz", null, cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        LibraryCopies("cancelled-linux-amd64.tar.gz").Should().Be(0);
    }
}
