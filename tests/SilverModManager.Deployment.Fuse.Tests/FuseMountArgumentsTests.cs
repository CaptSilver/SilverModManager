using FluentAssertions;
using NexusMods.Deployment.Fuse.Mount;
using NexusMods.Deployment.Vfs;
using NexusMods.Paths;
using NexusMods.Platform.Proton.Launch;

namespace NexusMods.Deployment.Fuse.Tests;

public class FuseMountArgumentsTests
{
    private static FuseMountOptions Options(Func<FuseMountOptions, FuseMountOptions>? change = null)
    {
        var options = FuseMountOptions.Default(FileSystem.Shared.FromUnsanitizedFullPath("/tmp/silvermod-mount-test"));
        return change?.Invoke(options) ?? options;
    }

    [Fact]
    public void Default_IsFsNameSubtypeAndDefaultPermissions()
    {
        FuseMountArguments.SessionArguments(Options())
            .Should().Equal("-o", "fsname=silvermod,subtype=silvermod,default_permissions");
    }

    [Fact]
    public void AllowOther_IsAppended()
    {
        FuseMountArguments.SessionArguments(Options(o => o with { AllowOther = true }))
            .Should().Equal("-o", "fsname=silvermod,subtype=silvermod,default_permissions,allow_other");
    }

    [Fact]
    public void DefaultPermissionsOff_IsDropped()
    {
        FuseMountArguments.SessionArguments(Options(o => o with { DefaultPermissions = false }))
            .Should().Equal("-o", "fsname=silvermod,subtype=silvermod");
    }

    [Fact]
    public void CustomNames_AreUsed()
    {
        FuseMountArguments.SessionArguments(Options(o => o with { FsName = "x", Subtype = "y", DefaultPermissions = false, AllowOther = true }))
            .Should().Equal("-o", "fsname=x,subtype=y,allow_other");
    }

    [Fact]
    public void Arguments_NeverCarryTheMountPointOrForeground()
    {
        var options = Options(o => o with { AllowOther = true });
        var args = FuseMountArguments.SessionArguments(options);

        args.Should().NotContain("-f");
        args.Should().NotContain(a => a.Contains(options.MountPoint.ToString()));
    }

    [Fact]
    public void FileSystemType_MatchesWhatLaunchStatusLooksFor()
    {
        FuseMountArguments.FileSystemType(Options()).Should().Be(LaunchStatusReport.VfsFileSystemType);
    }

    [Fact]
    public void FileSystemType_FollowsTheSubtype()
    {
        FuseMountArguments.FileSystemType(Options(o => o with { Subtype = "other" })).Should().Be("fuse.other");
    }
}
