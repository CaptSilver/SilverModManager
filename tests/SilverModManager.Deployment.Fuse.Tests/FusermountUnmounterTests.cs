using FluentAssertions;
using SilverModManager.Deployment.Fuse.Mount;
using NexusMods.Paths;

namespace SilverModManager.Deployment.Fuse.Tests;

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

    // Hands the runner's token to the test so it can tell whose cancellation reaches the process.
    private sealed class SlowRunner(TimeSpan duration, Func<IReadOnlyList<string>, bool> hangs) : IFusermountRunner
    {
        public List<string[]> Calls { get; } = [];

        public async Task<(int ExitCode, string StdErr)> Run(IReadOnlyList<string> args, CancellationToken token)
        {
            Calls.Add(args.ToArray());
            await Task.Delay(hangs(args) ? Timeout.InfiniteTimeSpan : duration, token);
            return (0, "");
        }
    }

    [Fact]
    public async Task ACancelledCaller_DoesNotAbortTheRunningUnmount()
    {
        var runner = new SlowRunner(TimeSpan.FromMilliseconds(300), _ => false);
        using var caller = new CancellationTokenSource();
        var unmount = new FusermountUnmounter(runner).Unmount(MountPoint, lazy: false, caller.Token);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        await caller.CancelAsync();
        var result = await unmount;

        result.Status.Should().Be(UnmountStatus.Unmounted, "a fusermount3 killed halfway can leave the mount behind");
        runner.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task AnUnmountThatOutlastsItsDeadline_FallsBackToALazyUnmount()
    {
        var runner = new SlowRunner(TimeSpan.Zero, args => args[0] == "-u");
        var unmounter = new FusermountUnmounter(runner) { Deadline = TimeSpan.FromMilliseconds(100) };

        var result = await unmounter.Unmount(MountPoint, lazy: false, CancellationToken.None);

        result.Status.Should().Be(UnmountStatus.Unmounted);
        runner.Calls.Select(c => c[0]).Should().Equal("-u", "-uz");
    }

    [Fact]
    public async Task ALazyUnmountThatOutlastsItsDeadline_IsFailedInsteadOfHanging()
    {
        var runner = new SlowRunner(TimeSpan.Zero, _ => true);
        var unmounter = new FusermountUnmounter(runner) { Deadline = TimeSpan.FromMilliseconds(100) };

        var result = await unmounter.Unmount(MountPoint, lazy: true, CancellationToken.None);

        result.Status.Should().Be(UnmountStatus.Failed);
        result.StdErr.Should().Contain("did not finish");
        runner.Calls.Should().ContainSingle();
    }
}
