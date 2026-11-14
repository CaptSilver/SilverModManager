using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Deployment.Fuse.Backend;
using SilverModManager.Deployment.Fuse.Kernel;
using SilverModManager.Deployment.Fuse.Mount;
using SilverModManager.Deployment.Vfs;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Native;

namespace SilverModManager.Deployment.Fuse.Tests;

/// <summary>
/// A daemon killed with a game folder mounted leaves the kernel's mount behind with nothing serving it. Recovery has to
/// detach it and promote what had been staged.
/// </summary>
[Collection(MountingTests.Name)]
public class DaemonKillRecoveryTests : IDisposable
{
    // Observed here (kernel 7.3, libfuse3): a stat of the mount point after the daemon is killed fails with ECONNREFUSED,
    // not the ENOTCONN the design expected. Either means a dead connection, so both are accepted.
    private const int NotConnected = 107;
    private const int ConnectionRefused = 111;
    private static readonly GameInstallMetadataId InstallationId = GameInstallMetadataId.From(0xABC);
    private static readonly LoadoutId Target = LoadoutId.From(0x77);
    private static readonly GamePath Root = new(LocationId.Game, "Data");

    private readonly AbsolutePath _directory = FileSystem.Shared
        .GetKnownPath(KnownPath.TempDirectory)
        .Combine($"fuse-daemon-kill-{Guid.NewGuid():N}");

    private Process? _holder;
    private string? _mountPoint;

    public void Dispose()
    {
        // A failed test must not leave the holder or its mount behind.
        if (_holder is { HasExited: false }) _holder.Kill();
        _holder?.Dispose();
        if (_mountPoint is not null) ForceUnmount(_mountPoint);
        if (_directory.DirectoryExists()) _directory.DeleteDirectory(recursive: true);
    }

    private static void ForceUnmount(string mountPoint)
    {
        try
        {
            using var process = Process.Start("fusermount3", ["-uz", "--", mountPoint]);
            process?.WaitForExit(5000);
        }
        catch (Exception)
        {
            // Nothing is mounted, or fusermount3 is missing; either way there is nothing left to undo.
        }
    }

    // The helper is built through a ProjectReference without referencing its assembly, so its output sits beside this project's under tests/.
    private static string HostDll()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var helper = Path.Combine(dir.FullName, "silvermod-testhost", "bin");
            if (!Directory.Exists(helper)) continue;

            var dll = Directory.EnumerateFiles(helper, "silvermod-testhost.dll", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (dll is not null) return dll;
        }
        throw new FileNotFoundException("silvermod-testhost.dll not found; build tests/silvermod-testhost");
    }

    private async Task<Process> StartHolder(AbsolutePath backing, AbsolutePath stagingLocation)
    {
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { HostDll(), "mount-holder", backing.ToString(), stagingLocation.ToString() }) info.ArgumentList.Add(arg);
        var process = Process.Start(info) ?? throw new InvalidOperationException("Failed to start silvermod-testhost");

        string? line = null;
        try
        {
            line = await process.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
        catch (TimeoutException)
        {
        }
        if (line != "mounted")
        {
            process.Kill();
            throw new InvalidOperationException($"the holder did not mount: '{line}'");
        }
        return process;
    }

    private static bool IsMounted(string mountPoint)
    {
        var (parent, leaf) = MountTable.SplitForLookup(mountPoint);
        return MountTable.IsMounted(parent, leaf, "fuse.silvermod");
    }

    [Fact, Trait("RequiresFuse", "True")]
    public async Task KillingTheDaemonLeavesADeadMountThatRecoverDetachesAndPromotes()
    {
        RequiresFuse.SkipUnlessAvailable();
        var data = _directory.Combine("game/Data");
        data.CreateDirectory();
        var stagingLocation = _directory.Combine("staging");
        var staging = new StagingDirectory(stagingLocation, InstallationId);
        var staged = staging.PathFor(RelativePath.FromUnsanitizedInput("new.esp"));
        staged.Parent.CreateDirectory();
        await staged.WriteAllBytesAsync("staged bytes"u8.ToArray());

        _mountPoint = data.ToString();
        _holder = await StartHolder(data, stagingLocation);
        IsMounted(_mountPoint).Should().BeTrue("the holder reported its mount");

        // By explicit pid: the daemon gets no chance to unmount.
        var pid = _holder.Id;
        using (var kill = Process.Start("kill", ["-9", pid.ToString()])) kill!.WaitForExit();
        await _holder.WaitForExitAsync(TestContext.Current.CancellationToken);

        // This settles what a path lookup of a mount point does once its daemon is dead.
        var errno = LibcStat.TryStat(_mountPoint, out _);
        errno.Should().BeOneOf([NotConnected, ConnectionRefused], $"observed errno {errno} on the mount point after the daemon was killed");
        IsMounted(_mountPoint).Should().BeTrue("the kernel keeps listing a mount nothing serves");
        // The backend finds the mount point this way before it looks it up in the mount table, so a stat that fails
        // must not lose it: the parent's listing still names it.
        FusePreflight.RealDirectory(data).Should().Be(data);

        var sink = new RecordingSink();
        var promoter = new StagingPromoter(staging, Root, sink, new XxHash3FileHasher(), NullLogger<StagingPromoter>.Instance, Size.From(new VfsSettings().PromotionSizeCapBytes));
        var tracker = Substitute.For<IProcessTracker>();
        tracker.FindGameProcesses(Arg.Any<GameInstallation>()).Returns(Array.Empty<TrackedProcess>());
        var recovery = new MountRecovery(tracker, new FusermountUnmounter(new CliFusermountRunner()), NullLogger.Instance);

        var report = await recovery.Run(Installation(data), data, mounted: true, staging, promoter, Target, TestContext.Current.CancellationToken);

        report.Status.Should().Be(MountRecoveryStatus.Promoted);
        report.WasMounted.Should().BeTrue();
        IsMounted(_mountPoint).Should().BeFalse();
        var record = sink.Records.Should().ContainSingle().Subject;
        record.Loadout.Should().Be(Target);
        record.Files.Select(f => f.Path.ToString()).Should().Equal("new.esp");
    }

    private static GameInstallation Installation(AbsolutePath data)
    {
        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns("Test Game");
        var result = new GameLocatorResult
        {
            Game = game,
            Path = data.Parent,
            Store = GameStore.Steam,
            StoreIdentifier = "42",
            LocatorIds = ImmutableArray<LocatorId>.Empty,
            Locator = Substitute.For<IGameLocator>(),
            Platform = OSPlatform.Windows,
        };
        return new GameInstallation(result, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, data.Parent)));
    }

    private sealed class RecordingSink : IPromotionSink
    {
        public List<(LoadoutId Loadout, GamePath Root, IReadOnlyList<PromotedFile> Files, IReadOnlyList<RelativePath> Deleted)> Records { get; } = [];

        public Task Backup(IReadOnlyList<(FileIdentity Identity, Size Size, IStreamFactory Stream)> files, CancellationToken token = default) => Task.CompletedTask;

        public Task Record(LoadoutId loadout, GamePath root, IReadOnlyList<PromotedFile> files, IReadOnlyList<RelativePath> deleted, CancellationToken token = default)
        {
            Records.Add((loadout, root, files, deleted));
            return Task.CompletedTask;
        }
    }
}
