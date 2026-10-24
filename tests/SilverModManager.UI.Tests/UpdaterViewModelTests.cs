using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SilverModManager.App.UI.Overlays.Updater;
using SilverModManager.Networking.GitHub.DTOs;
using SilverModManager.Sdk;

namespace SilverModManager.UI.Tests;

public class UpdaterViewModelTests : IDisposable
{
    private static readonly byte[] NewBytes = "new appimage"u8.ToArray();

    private readonly string _directory = Directory.CreateTempSubdirectory("updater-vm-").FullName;
    private readonly RecordingRestarter _restarter = new();

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static Asset MakeAsset(string digest) => new(
        "https://api.example.com/asset", 1, "SilverModManager.AppImage", "application/octet-stream", "uploaded", NewBytes.Length, 0,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "https://example.com/SilverModManager.AppImage", digest
    );

    private string CreateTarget()
    {
        var target = Path.Combine(_directory, "SilverModManager.AppImage");
        File.WriteAllText(target, "old");
        return target;
    }

    private UpdaterViewModel Create(string? appImagePath, string digest, InstallationMethod method = InstallationMethod.AppImage)
    {
        var installer = new AppImageUpdateInstaller(NullLogger.Instance, async (_, destination, progress, token) =>
        {
            progress(0.5);
            await File.WriteAllBytesAsync(destination, NewBytes, token);
        });

        return new UpdaterViewModel(
            osInterop: Substitute.For<IOSInterop>(),
            settingsManager: null!,
            installationMethod: method,
            currentVersion: new Version("1.0.0"),
            latestVersion: new Version("2.0.0"),
            releaseWebUri: new Uri("https://example.com"),
            assetDownloadUri: new Uri("https://example.com/SilverModManager.AppImage"),
            inAppUpdate: new InAppUpdate(installer, MakeAsset(digest), appImagePath, _restarter, NullLogger.Instance)
        );
    }

    private sealed class RecordingRestarter : IAppRestarter
    {
        public List<string> Restarted { get; } = [];
        public void Restart(string appImagePath) => Restarted.Add(appImagePath);
    }

    private static string DigestOf(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

    [Fact]
    public async Task Test_UpdateNow_InstallsThenOffersRestart()
    {
        var target = CreateTarget();
        var vm = Create(target, DigestOf(NewBytes));
        vm.CanUpdateInApp.Should().BeTrue();
        vm.State.Should().Be(UpdateState.Available);

        await vm.UpdateNowAsync();

        vm.State.Should().Be(UpdateState.ReadyToRestart);
        (await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken)).Should().Equal(NewBytes);
        _restarter.Restarted.Should().BeEmpty();

        vm.RestartNow();
        _restarter.Restarted.Should().Equal(target);
    }

    [Fact]
    public async Task Test_UpdateNow_DigestMismatch_ShowsAnErrorAndLeavesTheTarget()
    {
        var target = CreateTarget();
        var vm = Create(target, DigestOf("other"u8.ToArray()));

        await vm.UpdateNowAsync();

        vm.State.Should().Be(UpdateState.Failed);
        vm.StatusMessage.Should().Contain("SHA-256");
        File.ReadAllText(target).Should().Be("old");
        vm.RestartNow();
        _restarter.Restarted.Should().BeEmpty();
    }

    [Fact]
    public async Task Test_UpdateNow_WithoutAppImagePath_FailsWithAMessage()
    {
        var vm = Create(null, DigestOf(NewBytes));

        await vm.UpdateNowAsync();

        vm.State.Should().Be(UpdateState.Failed);
        vm.StatusMessage.Should().Contain("APPIMAGE");
    }

    [Fact]
    public async Task Test_UpdateNow_CancelledByClosing_LeavesTheTarget()
    {
        var target = CreateTarget();
        var vm = Create(target, DigestOf(NewBytes));
        vm.CancelUpdate();

        await vm.UpdateNowAsync();

        vm.State.Should().Be(UpdateState.Available);
        File.ReadAllText(target).Should().Be("old");
    }

    [Fact]
    public void Test_NoInAppUpdate_WhenNotAnAppImage()
    {
        var vm = new UpdaterViewModel(Substitute.For<IOSInterop>(), null!, InstallationMethod.Archive, new Version("1.0.0"), new Version("2.0.0"),
            new Uri("https://example.com"), new Uri("https://example.com/a.zip"), inAppUpdate: null);
        vm.CanUpdateInApp.Should().BeFalse();
    }

    [Fact]
    public void Test_RestartStartInfo_KeepsArgumentsAndDropsAppImageEnvironment()
    {
        var env = new Dictionary<string, string?> { ["APPIMAGE"] = "/old", ["APPDIR"] = "/tmp/.mount_x", ["OWD"] = "/home", ["ARGV0"] = "x", ["HOME"] = "/home/u" };
        var info = ProcessAppRestarter.BuildStartInfo("/apps/New.AppImage", ["--flag", "a b"], env, waitForPid: 1234);

        info.ArgumentList.Skip(2).Should().Equal("sh", "1234", "/apps/New.AppImage", "--flag", "a b");
        info.Environment.Should().NotContainKeys("APPIMAGE", "APPDIR", "OWD", "ARGV0");
        info.Environment["HOME"].Should().Be("/home/u");
        info.UseShellExecute.Should().BeFalse();
    }

    [Fact]
    public async Task Test_RestartStartInfo_StartsTheAppOnlyAfterTheOldProcessHasExited()
    {
        // The new instance opens the database, which the old one holds until it has exited.
        var marker = Path.Combine(_directory, "started");
        var app = Path.Combine(_directory, "New.AppImage");
        await File.WriteAllTextAsync(app, "#!/bin/sh\nprintf '%s' \"$*\" > \"$(dirname \"$0\")/started\"\n", TestContext.Current.CancellationToken);
        File.SetUnixFileMode(app, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        using var oldInstance = System.Diagnostics.Process.Start("sleep", "60")!;
        try
        {
            var info = ProcessAppRestarter.BuildStartInfo(app, ["--flag", "a b"], new Dictionary<string, string?> { ["PATH"] = "/usr/bin:/bin" }, oldInstance.Id);
            using var waiter = System.Diagnostics.Process.Start(info)!;

            await Task.Delay(TimeSpan.FromMilliseconds(600), TestContext.Current.CancellationToken);
            File.Exists(marker).Should().BeFalse();

            oldInstance.Kill();
            await oldInstance.WaitForExitAsync(TestContext.Current.CancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await waiter.WaitForExitAsync(timeout.Token);

            waiter.ExitCode.Should().Be(0);
            (await File.ReadAllTextAsync(marker, TestContext.Current.CancellationToken)).Should().Be("--flag a b");
        }
        finally
        {
            if (!oldInstance.HasExited) oldInstance.Kill();
        }
    }
}
