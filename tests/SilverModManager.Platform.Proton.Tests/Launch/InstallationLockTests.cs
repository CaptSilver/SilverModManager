using System.Runtime.InteropServices;
using FluentAssertions;
using SilverModManager.Platform.Proton.Launch;

namespace SilverModManager.Platform.Proton.Tests.Launch;

public class InstallationLockTests : IDisposable
{
    private readonly string _runtimeDir = Directory.CreateTempSubdirectory("silvermod-lock-").FullName;

    public void Dispose() => Directory.Delete(_runtimeDir, recursive: true);

    private string PathFor(string store = "steam", string storeId = "1091500") =>
        InstallationLock.PathFor(_runtimeDir, store, storeId);

    [Fact]
    public void PathFor_PutsTheLockUnderTheRuntimeDir()
    {
        PathFor().Should().Be(Path.Combine(_runtimeDir, "silvermod", "launch-steam-1091500.lock"));
    }

    [Fact]
    public void PathFor_SanitisesStoreIds()
    {
        InstallationLock.PathFor(_runtimeDir, "gog", "../a b")
            .Should().Be(Path.Combine(_runtimeDir, "silvermod", "launch-gog-.._a_b.lock"));
    }

    [Fact]
    public void TryAcquire_FailsWhileAnotherProcessHoldsTheLock_AndReadsItsState()
    {
        var path = PathFor();
        using var holder = TestHost.StartHolder(path, seconds: 30);
        try
        {
            using var taker = new InstallationLock(path);
            taker.TryAcquire(TimeSpan.FromMilliseconds(10)).Should().BeFalse();
            taker.IsHeld.Should().BeFalse();

            var state = taker.ReadState();
            state.Should().NotBeNull();
            state!.Pid.Should().Be(holder.Id);
            state.Loadout.Should().Be("test-loadout");
            state.Phase.Should().Be(LaunchPhase.Running);
            state.IsLeftover.Should().BeFalse();
        }
        finally
        {
            holder.Kill();
            holder.WaitForExit();
        }
    }

    [Fact]
    public void TryAcquire_SucceedsOnceTheHolderExits()
    {
        var path = PathFor();
        using (var holder = TestHost.StartHolder(path, seconds: 30))
        {
            holder.Kill();
            holder.WaitForExit();
        }

        using var taker = new InstallationLock(path);
        taker.TryAcquire().Should().BeTrue();
        taker.IsHeld.Should().BeTrue();
    }

    [Fact]
    public void ReadState_ReportsLeftoverWhenTheLockIsFree()
    {
        var path = PathFor();
        using (var holder = TestHost.StartHolder(path, seconds: 30))
        {
            holder.Kill();
            holder.WaitForExit();
        }

        using var reader = new InstallationLock(path);
        var state = reader.ReadState();
        state.Should().NotBeNull();
        state!.IsLeftover.Should().BeTrue();
        state.Phase.Should().Be(LaunchPhase.Running);
    }

    [Fact]
    public void ReadState_IsNullWhenNothingWasEverWritten()
    {
        using var reader = new InstallationLock(PathFor());
        reader.ReadState().Should().BeNull();
    }

    [Fact]
    public void ChildOfTheHolder_DoesNotKeepTheLockAfterTheHolderExits()
    {
        var path = PathFor();
        var (exit, output) = TestHost.Run("spawn-child-and-exit", path);
        exit.Should().Be(0, output);
        var childPid = int.Parse(output);
        try
        {
            // The child is still running; if the lock fd leaked into it, the lock would still be held.
            System.Diagnostics.Process.GetProcessById(childPid).HasExited.Should().BeFalse();

            using var taker = new InstallationLock(path);
            taker.TryAcquire().Should().BeTrue("the lock descriptor must be close-on-exec");
        }
        finally
        {
            System.Diagnostics.Process.GetProcessById(childPid).Kill();
        }
    }

    [Fact]
    public void TwoInstallations_DoNotContend()
    {
        using var first = new InstallationLock(PathFor(storeId: "1091500"));
        using var second = new InstallationLock(PathFor(storeId: "489830"));
        using var third = new InstallationLock(PathFor(store: "gog", storeId: "1091500"));

        first.TryAcquire().Should().BeTrue();
        second.TryAcquire().Should().BeTrue();
        third.TryAcquire().Should().BeTrue();
    }

    [Fact]
    public void Probe_ReportsHeldOnlyWhileAnotherHolderHasIt()
    {
        var path = PathFor();
        using var probe = new InstallationLock(path);
        probe.Probe().Should().BeFalse();

        using var holder = new InstallationLock(path);
        holder.TryAcquire().Should().BeTrue();
        probe.Probe().Should().BeTrue();

        holder.Dispose();
        probe.Probe().Should().BeFalse();
    }

    [Fact]
    public void Probe_DoesNotLeaveTheLockTaken()
    {
        var path = PathFor();
        using var probe = new InstallationLock(path);
        probe.Probe();

        using var taker = new InstallationLock(path);
        taker.TryAcquire(TimeSpan.Zero).Should().BeTrue();
    }

    [Fact]
    public void TryAcquire_RetriesOnceWhenAProbeHoldsTheLockMomentarily()
    {
        var path = PathFor();
        using var momentary = new InstallationLock(path);
        momentary.TryAcquire().Should().BeTrue();

        using var taker = new InstallationLock(path);
        var release = Task.Run(() =>
        {
            Thread.Sleep(50);
            momentary.Dispose();
        });

        taker.TryAcquire(TimeSpan.FromMilliseconds(500)).Should().BeTrue();
        release.Wait();
    }

    [Fact]
    public void TryAcquire_DiscardsAStaleStateFile()
    {
        var path = PathFor();
        using (var old = new InstallationLock(path))
        {
            old.TryAcquire().Should().BeTrue();
            old.WriteState(1, "old", LaunchPhase.Finishing, "");
            old.WriteWrapperKey("wrapper", "99");
        }

        using var taker = new InstallationLock(path);
        taker.TryAcquire().Should().BeTrue();
        taker.ReadState().Should().BeNull();
    }

    [Fact]
    public void WriteWrapperKey_KeepsTheHolderKeysAndPhase()
    {
        var path = PathFor();
        using var holder = new InstallationLock(path);
        holder.TryAcquire().Should().BeTrue();
        holder.WriteState(1234, "my loadout", LaunchPhase.Preparing, "");

        // Written by a different process that does not hold the lock, as the Steam wrapper does.
        var (exit, output) = TestHost.Run("write-wrapper", path, "777");
        exit.Should().Be(0, output);

        var state = holder.ReadState()!;
        state.Pid.Should().Be(1234);
        state.Loadout.Should().Be("my loadout");
        state.Phase.Should().Be(LaunchPhase.Preparing);
        state.Values["wrapper"].Should().Be("777");
    }

    [Fact]
    public void WriteState_KeepsKeysTheWrapperAdded()
    {
        var path = PathFor();
        using var holder = new InstallationLock(path);
        holder.TryAcquire().Should().BeTrue();
        holder.WriteState(1234, "loadout", LaunchPhase.Preparing, "");
        holder.WriteWrapperKey("wrapper", "777");

        holder.WriteState(1234, "loadout", LaunchPhase.Running, "");

        var state = holder.ReadState()!;
        state.Phase.Should().Be(LaunchPhase.Running);
        state.Values["wrapper"].Should().Be("777");
    }

    [Fact]
    public void WriteState_RecordsTheWineDllOverridesAndReadStateParsesThem()
    {
        var path = PathFor();
        using var holder = new InstallationLock(path);
        holder.TryAcquire().Should().BeTrue();

        holder.WriteState(1234, "loadout", LaunchPhase.Running, "winmm=n,b;version=n,b");

        var state = holder.ReadState()!;
        state.Values["WineDllOverrides"].Should().Be("winmm=n,b;version=n,b");
        state.WineDllOverrides.Select(o => o.ToString()).Should().Equal("winmm=n,b", "version=n,b");
    }

    [Fact]
    public void WriteState_ReplacesTheOverridesOfAnEarlierLaunch()
    {
        var path = PathFor();
        using var holder = new InstallationLock(path);
        holder.TryAcquire().Should().BeTrue();
        holder.WriteState(1234, "loadout", LaunchPhase.Running, "winmm=n,b");

        holder.WriteState(1234, "loadout", LaunchPhase.Running, "");

        holder.ReadState()!.WineDllOverrides.Should().BeEmpty();
    }

    [Fact]
    public void ReadState_WithoutTheKeyReportsNone()
    {
        var path = PathFor();
        using var holder = new InstallationLock(path);
        holder.TryAcquire().Should().BeTrue();
        File.WriteAllText(Path.ChangeExtension(path, ".state"), "pid=1234\nloadout=old\nphase=running\n");

        holder.ReadState()!.WineDllOverrides.Should().BeEmpty();
    }

    [Fact]
    public void WriteWrapperKey_ReplacesAnEarlierValueForTheSameKey()
    {
        var path = PathFor();
        using var holder = new InstallationLock(path);
        holder.TryAcquire().Should().BeTrue();
        holder.WriteState(1, "loadout", LaunchPhase.Running, "");
        holder.WriteWrapperKey("wrapper", "1");
        holder.WriteWrapperKey("wrapper", "2");

        holder.ReadState()!.Values["wrapper"].Should().Be("2");
    }

    [Fact]
    public void WriteState_WritesPlainKeyValueLinesWithoutAByteOrderMark()
    {
        var path = PathFor();
        using var holder = new InstallationLock(path);
        holder.TryAcquire().Should().BeTrue();
        holder.WriteState(1234, "loadout", LaunchPhase.Running, "");

        // Shell scripts and the support bundle read this file too; a BOM would glue itself to the first key.
        File.ReadAllBytes(holder.StatePath).Should().Equal("pid=1234\nloadout=loadout\nphase=running\nWineDllOverrides=\n"u8.ToArray());
    }

    [Fact]
    public void WriteState_LeavesNoTemporaryFilesBehind()
    {
        var path = PathFor();
        using var holder = new InstallationLock(path);
        holder.TryAcquire().Should().BeTrue();
        holder.WriteState(1, "loadout", LaunchPhase.Running, "");
        holder.WriteWrapperKey("k", "v");

        Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName)
            .Should().BeEquivalentTo("launch-steam-1091500.lock", "launch-steam-1091500.state");
    }

    [Fact]
    public void Release_FreesTheLockWhileAForkedChildStillHasACopyOfTheDescriptor()
    {
        // Between fork and exec, a child started by any thread of this process holds a copy of every descriptor,
        // O_CLOEXEC or not. A duplicate descriptor stands in for that copy: both share the open file the flock
        // belongs to.
        var path = PathFor();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var fd = open(path, O_RDWR | O_CREAT | O_CLOEXEC, 0b110_000_000);
        fd.Should().BeGreaterThanOrEqualTo(0);
        var childCopy = -1;
        var released = false;
        try
        {
            flock(fd, LOCK_EX | LOCK_NB).Should().Be(0);
            childCopy = fcntl(fd, F_DUPFD_CLOEXEC, 0);
            childCopy.Should().BeGreaterThanOrEqualTo(0);

            InstallationLock.Release(fd);
            released = true;

            using var taker = new InstallationLock(path);
            taker.TryAcquire(TimeSpan.Zero).Should().BeTrue();
        }
        finally
        {
            if (!released) close(fd);
            if (childCopy >= 0) close(childCopy);
        }
    }

    private const int O_RDWR = 2;
    private const int O_CREAT = 0x40;
    private const int O_CLOEXEC = 0x80000;
    private const int F_DUPFD_CLOEXEC = 1030;
    private const int LOCK_EX = 2;
    private const int LOCK_NB = 4;

    [DllImport("libc", SetLastError = true)]
    private static extern int open(string path, int flags, uint mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int flock(int fd, int operation);

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int fd, int command, int argument);

    [DllImport("libc")]
    private static extern int close(int fd);
}
