using FluentAssertions;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.Tests;

public class OwnProcessFamilyTests : IDisposable
{
    private const int OwnPid = 4321;
    private const int Helper = 4300;
    private const int Other = 5000;

    private readonly string _folder = Directory.CreateTempSubdirectory("silvermod-own-family-").FullName;
    private readonly Dictionary<int, string> _exeLinks = [];

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    // The real path of a file in the scratch folder, which is what /proc/<pid>/exe reports for it.
    private string AppImage() => Path.Combine(ProcProcessTracker.RealPath(_folder), "SilverModManager.AppImage");

    private OwnProcessFamily Family(string? appImage)
        => new(name => name == "APPIMAGE" ? appImage : null, pid => _exeLinks.GetValueOrDefault(pid), OwnPid);

    [Fact]
    public void OurOwnPidIsInTheFamily_WhateverAppImageSays()
    {
        Family(appImage: null).Contains(OwnPid).Should().BeTrue();
        Family(AppImage()).Contains(OwnPid).Should().BeTrue();
    }

    [Fact]
    public void AProcessRunningTheAppImageFileIsInTheFamily()
    {
        _exeLinks[Helper] = AppImage();

        Family(AppImage()).Contains(Helper).Should().BeTrue();
    }

    [Fact]
    public void AProcessRunningAReplacedAppImageFileIsStillInTheFamily()
    {
        // A self-update swaps the file under the running mount; the kernel then reports the old inode as deleted.
        _exeLinks[Helper] = AppImage() + " (deleted)";

        Family(AppImage()).Contains(Helper).Should().BeTrue();
    }

    [Fact]
    public void TheAppImageVariableIsResolvedThroughSymlinks()
    {
        var link = Path.Combine(_folder, "link.AppImage");
        File.CreateSymbolicLink(link, AppImage());
        _exeLinks[Helper] = AppImage();

        Family(link).Contains(Helper).Should().BeTrue();
    }

    [Fact]
    public void AnUnrelatedProcessIsNotInTheFamily()
    {
        _exeLinks[Other] = "/usr/bin/wineserver";

        var family = Family(AppImage());

        family.Contains(Other).Should().BeFalse();
        family.Contains(Helper).Should().BeFalse("its exe cannot be read");
    }

    [Fact]
    public void AnotherAppImageIsNotInTheFamily()
    {
        _exeLinks[Other] = AppImage() + ".other";

        Family(AppImage()).Contains(Other).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void WithoutAnAppImageOnlyOurOwnPidIsInTheFamily(string? appImage)
    {
        _exeLinks[Helper] = AppImage();

        var family = Family(appImage);

        family.Contains(Helper).Should().BeFalse();
        family.Contains(OwnPid).Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(OwnPid)]
    public void ForeignDropsPidsThatMustNeverBeSignalled(int pid)
    {
        Family(appImage: null).Foreign([new TrackedProcess(pid, "x")]).Should().BeEmpty();
    }

    [Fact]
    public void ForeignKeepsOtherProcessesAndDropsTheHelper()
    {
        _exeLinks[Helper] = AppImage();
        var game = new TrackedProcess(Other, "game");

        Family(AppImage()).Foreign([new TrackedProcess(Helper, "helper"), game, new TrackedProcess(OwnPid, "me")])
            .Should().Equal(game);
    }

    [Fact]
    public void TheDefaultFamilyReadsProc()
    {
        OwnProcessFamily.Current.Contains(Environment.ProcessId).Should().BeTrue();
        OwnProcessFamily.Current.Contains(int.MaxValue - 1).Should().BeFalse();
    }
}
