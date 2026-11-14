using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Deployment.Fuse.Backend;
using SilverModManager.Deployment.Fuse.Mount;
using SilverModManager.Deployment.Vfs;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Deployment.Fuse.Tests.Backend;

/// <summary>
/// What recovery does with a dead session's mount and staged writes, against a real staging directory in a temp folder
/// and a fake unmounter, so no mount is needed.
/// </summary>
public class FuseRecoverTests : IDisposable
{
    private static readonly LoadoutId Target = LoadoutId.From(0x77);
    private static readonly GameInstallMetadataId InstallationId = GameInstallMetadataId.From(0xABC);
    private static readonly GamePath Root = new(LocationId.Game, "Data");

    private readonly AbsolutePath _directory = FileSystem.Shared
        .GetKnownPath(KnownPath.TempDirectory)
        .Combine($"fuse-recover-tests-{Guid.NewGuid():N}");

    private readonly IProcessTracker _tracker = Substitute.For<IProcessTracker>();
    private readonly IFuseUnmounter _unmounter = Substitute.For<IFuseUnmounter>();
    private readonly RecordingSink _sink = new();
    private readonly StagingDirectory _staging;
    private readonly StagingPromoter _promoter;
    private readonly GameInstallation _installation;

    public FuseRecoverTests()
    {
        _staging = new StagingDirectory(_directory.Combine("staging"), InstallationId);
        _promoter = new StagingPromoter(_staging, Root, _sink, new XxHash3FileHasher(), NullLogger<StagingPromoter>.Instance, Size.From(new VfsSettings().PromotionSizeCapBytes));
        _tracker.FindGameProcesses(Arg.Any<GameInstallation>()).Returns(Array.Empty<TrackedProcess>());
        _unmounter.Unmount(Arg.Any<AbsolutePath>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(new UnmountResult(UnmountStatus.Unmounted, 0, ""));
        _installation = Installation();
    }

    public void Dispose() => _directory.DeleteDirectory(recursive: true);

    private GameInstallation Installation()
    {
        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns("Test Game");
        var folder = _directory.Combine("game");
        var result = new GameLocatorResult
        {
            Game = game,
            Path = folder,
            Store = GameStore.Steam,
            StoreIdentifier = "42",
            LocatorIds = ImmutableArray<LocatorId>.Empty,
            Locator = Substitute.For<IGameLocator>(),
            Platform = OSPlatform.Windows,
        };
        return new GameInstallation(result, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, folder)));
    }

    private AbsolutePath MountPoint => _directory.Combine("game/Data");

    private Task<MountRecoveryReport> Recover(bool mounted, LoadoutId? target) =>
        new MountRecovery(_tracker, _unmounter, NullLogger.Instance).Run(_installation, MountPoint, mounted, _staging, _promoter, target, CancellationToken.None);

    private void Stage(string path, string text)
    {
        var file = _staging.PathFor(RelativePath.FromUnsanitizedInput(path));
        file.Parent.CreateDirectory();
        File.WriteAllBytes(file.ToString(), Encoding.UTF8.GetBytes(text));
    }

    private AbsolutePath WriteOther()
    {
        var other = _directory.Combine("other.bin");
        other.Parent.CreateDirectory();
        File.WriteAllText(other.ToString(), "other bytes");
        return other;
    }

    [Fact]
    public async Task StillRunningTouchesNothing()
    {
        _tracker.FindGameProcesses(Arg.Any<GameInstallation>()).Returns([new TrackedProcess(5000, "game.exe")]);
        Stage("new.esp", "bytes");

        var report = await Recover(mounted: true, Target);

        report.Status.Should().Be(MountRecoveryStatus.StillRunning);
        await _unmounter.DidNotReceiveWithAnyArgs().Unmount(default, default, default);
        _sink.Records.Should().BeEmpty();
        _staging.PathFor(RelativePath.FromUnsanitizedInput("new.esp")).FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task DetachesAStaleMountLazily()
    {
        var report = await Recover(mounted: true, Target);

        report.Status.Should().Be(MountRecoveryStatus.Detached);
        report.WasMounted.Should().BeTrue();
        await _unmounter.Received(1).Unmount(MountPoint, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AMountThatCannotBeDetachedStopsBeforeAnythingIsPromoted()
    {
        _unmounter.Unmount(Arg.Any<AbsolutePath>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(new UnmountResult(UnmountStatus.Failed, 1, "busy"));
        Stage("new.esp", "bytes");

        var act = () => Recover(mounted: true, Target);

        await act.Should().ThrowAsync<MountDetachFailedException>().WithMessage("*Data*busy*");
        _sink.Records.Should().BeEmpty("the files are still reachable through a live mount");
    }

    [Fact]
    public async Task FinishesAJournal()
    {
        Stage("new.esp", "bytes");
        var identity = await new XxHash3FileHasher().Hash(InstallationId, _staging.PathFor(RelativePath.FromUnsanitizedInput("new.esp")));
        new PromotionJournal(InstallationId, Target, Root, [new JournalEntry(RelativePath.FromUnsanitizedInput("new.esp"), JournalEntryKind.File, Size.From(5), identity, 0x1A4)])
            .Write(_staging.JournalFile);

        // No target: the journal knows its own.
        var report = await Recover(mounted: false, target: null);

        report.Status.Should().Be(MountRecoveryStatus.Promoted);
        report.PromotedFiles.Should().Be(1);
        _sink.Records.Should().ContainSingle().Which.Loadout.Should().Be(Target);
        _staging.JournalFile.FileExists.Should().BeFalse();
    }

    [Fact]
    public async Task PromotesStagedFilesIntoTheTarget()
    {
        Stage("new.esp", "bytes");

        var report = await Recover(mounted: true, Target);

        report.Status.Should().Be(MountRecoveryStatus.Promoted);
        report.WasMounted.Should().BeTrue();
        report.PromotedFiles.Should().Be(1);
        var record = _sink.Records.Should().ContainSingle().Subject;
        record.Loadout.Should().Be(Target);
        record.Root.Should().Be(Root);
    }

    [Fact]
    public async Task StagedFilesWithoutATargetReportNeedsTarget()
    {
        Stage("new.esp", "bytes");

        var report = await Recover(mounted: true, target: null);

        report.Status.Should().Be(MountRecoveryStatus.NeedsTarget);
        report.Staging.Should().Be(_staging.Root);
        report.WasMounted.Should().BeTrue();
        _sink.Records.Should().BeEmpty("a loadout is not guessed");
        _staging.PathFor(RelativePath.FromUnsanitizedInput("new.esp")).FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task NothingToRecoverIsIdempotent()
    {
        (await Recover(mounted: false, Target)).Status.Should().Be(MountRecoveryStatus.Nothing);
        (await Recover(mounted: false, Target)).Status.Should().Be(MountRecoveryStatus.Nothing);
        await _unmounter.DidNotReceiveWithAnyArgs().Unmount(default, default, default);
    }

    [Fact]
    public async Task APromotionIsNotRepeatedByASecondCall()
    {
        Stage("new.esp", "bytes");

        (await Recover(mounted: true, Target)).Status.Should().Be(MountRecoveryStatus.Promoted);
        (await Recover(mounted: false, Target)).Status.Should().Be(MountRecoveryStatus.Nothing);
        _sink.Records.Should().ContainSingle();
    }

    [Fact]
    public async Task ABlockedPromotionNamesTheRecoveryFolder()
    {
        Stage("new.esp", "bytes");
        // Hashed from other bytes: the file has changed since the journal was written.
        var stale = await new XxHash3FileHasher().Hash(InstallationId, WriteOther());
        new PromotionJournal(InstallationId, Target, Root, [new JournalEntry(RelativePath.FromUnsanitizedInput("new.esp"), JournalEntryKind.File, Size.From(5), stale, 0x1A4)])
            .Write(_staging.JournalFile);

        var report = await Recover(mounted: false, target: null);

        report.Status.Should().Be(MountRecoveryStatus.Blocked);
        report.Recovery.Should().NotBeNull();
        report.Recovery!.Value.InFolder(_staging.RecoveryRoot).Should().BeTrue();
        _sink.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task StagedFilesOverTheSizeCapAreLeftInStagingAndReportedOverCap()
    {
        Stage("new.esp", "more than four bytes");
        var capped = new StagingPromoter(_staging, Root, _sink, new XxHash3FileHasher(), NullLogger<StagingPromoter>.Instance, Size.From(4));

        var report = await new MountRecovery(_tracker, _unmounter, NullLogger.Instance).Run(_installation, MountPoint, mounted: false, _staging, capped, Target, CancellationToken.None);

        report.Status.Should().Be(MountRecoveryStatus.OverCap);
        report.Staging.Should().Be(_staging.Root);
        report.Recovery.Should().BeNull("nothing was set aside");
        _sink.Records.Should().BeEmpty();
        _staging.PathFor(RelativePath.FromUnsanitizedInput("new.esp")).FileExists.Should().BeTrue();
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
