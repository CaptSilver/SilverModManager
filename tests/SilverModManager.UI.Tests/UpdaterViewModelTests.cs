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
        var vm = new UpdaterViewModel(Substitute.For<IOSInterop>(), InstallationMethod.Archive, new Version("1.0.0"), new Version("2.0.0"),
            new Uri("https://example.com"), new Uri("https://example.com/a.zip"), inAppUpdate: null);
        vm.CanUpdateInApp.Should().BeFalse();
    }

    [Fact]
    public void Test_BrowserDownloadCommand_IsOffWithoutAnAssetAndOpensTheAssetWithOne()
    {
        var osInterop = Substitute.For<IOSInterop>();
        var withoutAsset = new UpdaterViewModel(osInterop, InstallationMethod.Flatpak, new Version("1.0.0"), new Version("2.0.0"),
            new Uri("https://example.com"), assetDownloadUri: null);
        var asset = new Uri("https://example.com/a.zip");
        var withAsset = new UpdaterViewModel(osInterop, InstallationMethod.Archive, new Version("1.0.0"), new Version("2.0.0"),
            new Uri("https://example.com"), asset);

        withoutAsset.CommandDownloadReleaseAssetInBrowser.CanExecute().Should().BeFalse();
        withoutAsset.CommandDownloadReleaseAssetInBrowser.Execute(R3.Unit.Default);
        osInterop.ReceivedCalls().Should().BeEmpty();
        withAsset.CommandDownloadReleaseAssetInBrowser.Execute(R3.Unit.Default);

        osInterop.Received(1).OpenUri(asset);
        osInterop.ReceivedCalls().Should().HaveCount(1);
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

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int dup(int descriptor);

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int close(int descriptor);

    /// <summary>A descriptor without close-on-exec, like the one the AppImage runtime leaves open in the app.</summary>
    private int OpenInheritableDescriptor(out Microsoft.Win32.SafeHandles.SafeFileHandle file)
    {
        file = File.OpenHandle(Path.Combine(_directory, "held"), FileMode.Create, FileAccess.ReadWrite);
        var descriptor = dup((int)file.DangerousGetHandle());
        descriptor.Should().BeGreaterThan(2);
        return descriptor;
    }

    /// <summary>Runs the restart shell with an app that writes the descriptors it was started with.</summary>
    private async Task<int[]> DescriptorsTheRestartedAppSees()
    {
        var listing = Path.Combine(_directory, "fds");
        var app = Path.Combine(_directory, "Fds.AppImage");
        await File.WriteAllTextAsync(app, "#!/bin/sh\nfor f in /proc/$$/fd/*; do echo \"${f##*/}\"; done > \"$(dirname \"$0\")/fds\"\n", TestContext.Current.CancellationToken);
        File.SetUnixFileMode(app, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        using var exited = System.Diagnostics.Process.Start("true")!;
        await exited.WaitForExitAsync(TestContext.Current.CancellationToken);
        var info = ProcessAppRestarter.BuildStartInfo(app, [], new Dictionary<string, string?> { ["PATH"] = "/usr/bin:/bin" }, exited.Id);
        using var waiter = System.Diagnostics.Process.Start(info)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await waiter.WaitForExitAsync(timeout.Token);

        return (await File.ReadAllLinesAsync(listing, TestContext.Current.CancellationToken)).Select(int.Parse).ToArray();
    }

    [Fact]
    public void Test_InheritableDescriptors_ListsOnlyThoseWithoutCloseOnExec()
    {
        var descriptor = OpenInheritableDescriptor(out var file);
        using (file)
        {
            try
            {
                var listed = ProcessAppRestarter.InheritableDescriptors();
                listed.Should().Contain(descriptor);
                listed.Should().NotContain((int)file.DangerousGetHandle(), ".NET opens files close-on-exec");
                listed.Should().OnlyContain(fd => fd > 2, "standard input and output stay with the new app");
            }
            finally { close(descriptor); }
        }
    }

    [Fact]
    public async Task Test_Restart_DoesNotHandTheOldInstancesDescriptorsToTheNewApp()
    {
        // Inherited by the new AppImage, the old runtime's keep-alive descriptor kept that runtime and its mount alive.
        var descriptor = OpenInheritableDescriptor(out var file);
        using (file)
        {
            try
            {
                (await DescriptorsTheRestartedAppSees()).Should().Contain(descriptor, "without the fix the app inherits it");

                ProcessAppRestarter.MarkCloseOnExec([descriptor]);

                (await DescriptorsTheRestartedAppSees()).Should().NotContain(descriptor);
            }
            finally { close(descriptor); }
        }
    }
}
