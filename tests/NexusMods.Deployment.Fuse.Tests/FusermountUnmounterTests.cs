using FluentAssertions;
using NexusMods.Deployment.Fuse.Mount;
using NexusMods.Paths;

namespace NexusMods.Deployment.Fuse.Tests;

public class FusermountUnmounterTests
{
    private sealed class FakeRunner(int exitCode, string stdErr = "") : IFusermountRunner
    {
        public List<string[]> Calls { get; } = [];

        public Task<(int ExitCode, string StdErr)> Run(IReadOnlyList<string> args, CancellationToken token)
        {
            Calls.Add(args.ToArray());
            return Task.FromResult((exitCode, stdErr));
        }
    }

    private static readonly AbsolutePath MountPoint = FileSystem.Shared.FromUnsanitizedFullPath("/tmp/silvermod-unmount-test");

    [Fact]
    public async Task ExitZero_IsUnmounted()
    {
        var runner = new FakeRunner(0);

        var result = await new FusermountUnmounter(runner).Unmount(MountPoint, lazy: false, CancellationToken.None);

        result.Status.Should().Be(UnmountStatus.Unmounted);
        runner.Calls.Should().ContainSingle().Which.Should().Equal("-u", "--", MountPoint.ToString());
    }

    [Fact]
    public async Task Lazy_AddsTheLazyFlag()
    {
        var runner = new FakeRunner(0);

        await new FusermountUnmounter(runner).Unmount(MountPoint, lazy: true, CancellationToken.None);

        runner.Calls.Should().ContainSingle().Which.Should().Equal("-uz", "--", MountPoint.ToString());
    }

    [Theory]
    [InlineData("fusermount3: entry for /tmp/x not found in /etc/mtab")]
    [InlineData("fusermount3: failed to unmount /tmp/x: Invalid argument")]
    public async Task NotAMountPoint_IsNotMounted(string stdErr)
    {
        var result = await new FusermountUnmounter(new FakeRunner(1, stdErr)).Unmount(MountPoint, false, CancellationToken.None);

        result.Status.Should().Be(UnmountStatus.NotMounted);
    }

    [Fact]
    public async Task BusyMount_IsFailedAndKeepsStdErr()
    {
        var result = await new FusermountUnmounter(new FakeRunner(1, "fusermount3: failed to unmount /tmp/x: Device or resource busy")).Unmount(MountPoint, false, CancellationToken.None);

        result.Status.Should().Be(UnmountStatus.Failed);
        result.ExitCode.Should().Be(1);
        result.StdErr.Should().Contain("busy");
    }
}
