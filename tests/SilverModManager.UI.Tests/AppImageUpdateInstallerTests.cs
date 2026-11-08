using System.Runtime.InteropServices;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.App.UI.Overlays.Updater;
using SilverModManager.Networking.GitHub.DTOs;

namespace SilverModManager.UI.Tests;

public class AppImageUpdateInstallerTests : IDisposable
{
    private static readonly byte[] OldBytes = "old appimage"u8.ToArray();
    private static readonly Architecture Cpu = RuntimeInformation.ProcessArchitecture;
    private static readonly byte[] NewBytes = ElfBytes.For(Cpu, filler: 0x42);

    private readonly string _directory = Directory.CreateTempSubdirectory("appimage-update-").FullName;
    private readonly string _target;

    public AppImageUpdateInstallerTests()
    {
        _target = Path.Combine(_directory, "SilverModManager.AppImage");
        File.WriteAllBytes(_target, OldBytes);
        File.SetUnixFileMode(_target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static Asset MakeAsset(string? digest) => new(
        Url: "https://api.example.com/asset",
        Id: 1,
        Name: "SilverModManager.AppImage",
        ContentType: "application/octet-stream",
        State: "uploaded",
        Size: NewBytes.Length,
        DownloadCount: 0,
        CreatedAt: DateTimeOffset.UnixEpoch,
        UpdatedAt: DateTimeOffset.UnixEpoch,
        BrowserDownloadUrl: "https://example.com/SilverModManager.AppImage",
        Digest: digest
    );

    private static string DigestOf(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    private List<string> DownloadDestinations { get; } = [];

    private AppImageUpdateInstaller MakeInstaller(byte[]? payload = null, Func<string, CancellationToken, Task>? beforeWrite = null)
    {
        return new AppImageUpdateInstaller(NullLogger.Instance, async (uri, destination, progress, cancellationToken) =>
        {
            uri.Should().Be(new Uri("https://example.com/SilverModManager.AppImage"));
            DownloadDestinations.Add(destination);
            if (beforeWrite is not null) await beforeWrite(destination, cancellationToken);
            await File.WriteAllBytesAsync(destination, payload ?? NewBytes, cancellationToken);
            progress(1.0);
        });
    }

    private string[] Files() => Directory.GetFiles(_directory);

    [Fact]
    public async Task Test_Install_ReplacesTheTargetWithTheVerifiedDownload()
    {
        var progress = new List<double>();
        await MakeInstaller().InstallAsync(MakeAsset(DigestOf(NewBytes)), _target, Cpu, progress.Add, TestContext.Current.CancellationToken);

        (await File.ReadAllBytesAsync(_target, TestContext.Current.CancellationToken)).Should().Equal(NewBytes);
        Files().Should().BeEquivalentTo([_target], because: "the temporary file is renamed over the target, not left beside it");
        progress.Should().Contain(1.0);
    }

    [Fact]
    public async Task Test_Install_DownloadsNextToTheTargetSoTheRenameStaysOnOneFilesystem()
    {
        await MakeInstaller().InstallAsync(MakeAsset(DigestOf(NewBytes)), _target, Cpu, _ => { }, TestContext.Current.CancellationToken);

        DownloadDestinations.Should().ContainSingle().Which.Should().StartWith(_directory + Path.DirectorySeparatorChar);
        DownloadDestinations[0].Should().NotBe(_target);
    }

    [Fact]
    public async Task Test_Install_KeepsTheOldModeAndAddsExecute()
    {
        await MakeInstaller().InstallAsync(MakeAsset(DigestOf(NewBytes)), _target, Cpu, _ => { }, TestContext.Current.CancellationToken);

        var mode = File.GetUnixFileMode(_target);
        mode.Should().HaveFlag(UnixFileMode.UserExecute);
        mode.Should().HaveFlag(UnixFileMode.GroupRead);
        mode.Should().NotHaveFlag(UnixFileMode.OtherRead);
    }

    [Fact]
    public async Task Test_Install_DigestMismatch_LeavesTheTargetAndRemovesTheTemporaryFile()
    {
        var act = () => MakeInstaller().InstallAsync(MakeAsset(DigestOf("something else"u8.ToArray())), _target, Cpu, _ => { }, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<AppImageUpdateException>()).Which.Message.Should().Contain("SHA-256");
        (await File.ReadAllBytesAsync(_target, TestContext.Current.CancellationToken)).Should().Equal(OldBytes);
        Files().Should().Equal(_target);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("sha1:da39a3ee5e6b4b0d3255bfef95601890afd80709")]
    [InlineData("sha256:not-hex")]
    public async Task Test_Install_MissingOrUnusableDigest_NeverInstalls(string? digest)
    {
        var act = () => MakeInstaller().InstallAsync(MakeAsset(digest), _target, Cpu, _ => { }, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<AppImageUpdateException>();
        (await File.ReadAllBytesAsync(_target, TestContext.Current.CancellationToken)).Should().Equal(OldBytes);
        Files().Should().Equal(_target);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Test_Install_WithoutAppImagePath_IsRefusedBeforeDownloading(string? appImage)
    {
        var act = () => MakeInstaller().InstallAsync(MakeAsset(DigestOf(NewBytes)), appImage, Cpu, _ => { }, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<AppImageUpdateException>()).Which.Message.Should().Contain("APPIMAGE");
        DownloadDestinations.Should().BeEmpty();
    }

    [Fact]
    public async Task Test_Install_MissingTargetFile_IsRefusedBeforeDownloading()
    {
        var act = () => MakeInstaller().InstallAsync(MakeAsset(DigestOf(NewBytes)), Path.Combine(_directory, "gone.AppImage"), Cpu, _ => { }, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<AppImageUpdateException>();
        DownloadDestinations.Should().BeEmpty();
    }

    [Fact]
    public async Task Test_Install_UnwritableDirectory_IsRefusedBeforeDownloading()
    {
        File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            if (Environment.UserName == "root") return; // root ignores directory modes
            var act = () => MakeInstaller().InstallAsync(MakeAsset(DigestOf(NewBytes)), _target, Cpu, _ => { }, TestContext.Current.CancellationToken);

            (await act.Should().ThrowAsync<AppImageUpdateException>()).Which.Message.Should().Contain("writable");
            DownloadDestinations.Should().BeEmpty();
        }
        finally
        {
            File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task Test_Install_Cancelled_LeavesTheTargetAndRemovesTheTemporaryFile()
    {
        using var cts = new CancellationTokenSource();
        var installer = MakeInstaller(beforeWrite: async (destination, token) =>
        {
            await File.WriteAllBytesAsync(destination, "partial"u8.ToArray(), token);
            await cts.CancelAsync();
            token.ThrowIfCancellationRequested();
        });

        var act = () => installer.InstallAsync(MakeAsset(DigestOf(NewBytes)), _target, Cpu, _ => { }, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await File.ReadAllBytesAsync(_target, TestContext.Current.CancellationToken)).Should().Equal(OldBytes);
        Files().Should().Equal(_target);
    }

    [Fact]
    public async Task Test_Install_FailedDownload_RemovesTheTemporaryFile()
    {
        var installer = MakeInstaller(beforeWrite: async (destination, token) =>
        {
            await File.WriteAllBytesAsync(destination, "partial"u8.ToArray(), token);
            throw new HttpRequestException("connection reset");
        });

        var act = () => installer.InstallAsync(MakeAsset(DigestOf(NewBytes)), _target, Cpu, _ => { }, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HttpRequestException>();
        (await File.ReadAllBytesAsync(_target, TestContext.Current.CancellationToken)).Should().Equal(OldBytes);
        Files().Should().Equal(_target);
    }

    private async Task AssertDownloadForAnotherCpuIsRefused(Architecture downloaded, Architecture running, string downloadedName, string runningName)
    {
        var payload = ElfBytes.For(downloaded);

        var act = () => MakeInstaller(payload).InstallAsync(MakeAsset(DigestOf(payload)), _target, running, _ => { }, TestContext.Current.CancellationToken);

        var message = (await act.Should().ThrowAsync<AppImageUpdateException>()).Which.Message;
        message.Should().Contain(downloadedName).And.Contain(runningName);
        (await File.ReadAllBytesAsync(_target, TestContext.Current.CancellationToken)).Should().Equal(OldBytes);
        Files().Should().Equal(_target);
    }

    [Fact]
    public Task AnAarch64DownloadIsRefusedOnAnX64Process() => AssertDownloadForAnotherCpuIsRefused(Architecture.Arm64, Architecture.X64, "aarch64", "x86_64");

    [Fact]
    public Task AnX86_64DownloadIsRefusedOnAnArm64Process() => AssertDownloadForAnotherCpuIsRefused(Architecture.X64, Architecture.Arm64, "x86_64", "aarch64");

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x7f, 0x45, 0x4c, 0x46 })]
    public async Task ADownloadThatIsNotAnElfIsRefused(byte[] payload)
    {
        var act = () => MakeInstaller(payload).InstallAsync(MakeAsset(DigestOf(payload)), _target, Cpu, _ => { }, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<AppImageUpdateException>();
        (await File.ReadAllBytesAsync(_target, TestContext.Current.CancellationToken)).Should().Equal(OldBytes);
        Files().Should().Equal(_target);
    }

    [Fact]
    public async Task ADownloadWithoutTheElfMagicIsRefused()
    {
        var payload = ElfBytes.For(Cpu);
        payload[1] = (byte)'X';

        var act = () => MakeInstaller(payload).InstallAsync(MakeAsset(DigestOf(payload)), _target, Cpu, _ => { }, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<AppImageUpdateException>();
        Files().Should().Equal(_target);
    }

    [Theory]
    [InlineData(Architecture.X64)]
    [InlineData(Architecture.Arm64)]
    public async Task ADownloadForTheRunningCpuIsRenamedIntoPlace(Architecture cpu)
    {
        var payload = ElfBytes.For(cpu, filler: 0x07);

        await MakeInstaller(payload).InstallAsync(MakeAsset(DigestOf(payload)), _target, cpu, _ => { }, TestContext.Current.CancellationToken);

        (await File.ReadAllBytesAsync(_target, TestContext.Current.CancellationToken)).Should().Equal(payload);
        Files().Should().Equal(_target);
    }
}
