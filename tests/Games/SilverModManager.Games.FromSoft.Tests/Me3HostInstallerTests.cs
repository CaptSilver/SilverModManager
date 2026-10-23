using FluentAssertions;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Games.FromSoft.Installers;
using NexusMods.Games.FromSoft.Tests.Fixtures;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Library;

namespace NexusMods.Games.FromSoft.Tests;

public class Me3HostInstallerTests(ITestOutputHelper outputHelper) : AEldenRingInstallerTests<Me3HostInstallerTests>(outputHelper)
{
    private static readonly string[] ReleaseMembers = ["bin/me3", "bin/win64/me3-launcher.exe", "bin/win64/me3_mod_host.dll"];

    private static string Me3Host(string path) => $"{LocationId.Me3Host}:{path}";

    private async Task<LibraryArchive.ReadOnly> AddTarball(params string[] paths)
    {
        var path = TemporaryFileManager.CreateFolder().Path / "me3-linux-amd64.tar.gz";
        await TestArchives.WriteTarGz(path, paths);
        return await RegisterLocalArchive(path);
    }

    [Fact]
    public async Task TheBinaryAndTheTwoWindowsComponentsLandInTheHostLocation()
    {
        var (result, targets) = await TryInstall<Me3HostInstaller>(await AddTarball(ReleaseMembers));

        result.IsSuccess.Should().BeTrue();
        targets.Should().BeEquivalentTo(Me3Host("bin/me3"), Me3Host("bin/win64/me3-launcher.exe"), Me3Host("bin/win64/me3_mod_host.dll"));
    }

    [Fact]
    public async Task MembersStoredWithALeadingDotSlashInstallTheSame()
    {
        var (result, targets) = await TryInstall<Me3HostInstaller>(await AddTarball(ReleaseMembers.Select(m => "./" + m).ToArray()));

        result.IsSuccess.Should().BeTrue();
        targets.Should().BeEquivalentTo(Me3Host("bin/me3"), Me3Host("bin/win64/me3-launcher.exe"), Me3Host("bin/win64/me3_mod_host.dll"));
    }

    [Fact]
    public async Task EverythingElseInTheReleaseIsDropped()
    {
        var members = ReleaseMembers.Concat(["LICENSE", "README.md", "completions/me3.bash", "share/applications/me3.desktop"]).ToArray();

        var (result, targets) = await TryInstall<Me3HostInstaller>(await AddTarball(members));

        result.IsSuccess.Should().BeTrue();
        targets.Should().HaveCount(3);
    }

    [Fact]
    public async Task TheWindowsZipIsNotSupportedAndTheReasonSaysWhy()
    {
        var archive = await AddNamed("me3-windows-amd64.zip", "bin/me3.exe", "bin/win64/me3-launcher.exe", "bin/win64/me3_mod_host.dll");

        var (result, targets) = await TryInstall<Me3HostInstaller>(archive);

        result.IsNotSupported(out var reason).Should().BeTrue();
        reason.Should().Be("This is not the me3 Linux release (no bin/me3); the Windows zip cannot be used here");
        targets.Should().BeEmpty();
    }

    [Fact]
    public async Task AReleaseMissingAWindowsComponentIsNotSupported()
    {
        var (result, _) = await TryInstall<Me3HostInstaller>(await AddTarball("bin/me3", "bin/win64/me3-launcher.exe"));

        result.IsNotSupported(out var reason).Should().BeTrue();
        reason.Should().Contain("bin/win64/me3_mod_host.dll");
    }
}
