using NexusMods.Sdk.Loadouts;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.IO;

namespace NexusMods.Deployment.Vfs.Tests;

/// <summary>
/// Promotion and recovery against a sink that records and can fail, so every crash point of the
/// journaled sequence can be hit without a database.
/// </summary>
public class PromotionTests : IDisposable
{
    private static readonly LoadoutId Target = LoadoutId.From(0x77);
    private static readonly GameInstallMetadataId Installation = GameInstallMetadataId.From(0xABC);

    private readonly AbsolutePath _directory = FileSystem.Shared
        .GetKnownPath(KnownPath.TempDirectory)
        .Combine($"vfs-promotion-tests-{Guid.NewGuid():N}");

    private readonly StagingDirectory _staging;
    private readonly FaultingSink _sink;

    public PromotionTests()
    {
        _staging = new StagingDirectory(_directory.Combine("staging"), Installation);
        _sink = new FaultingSink(_staging);
    }

    public void Dispose() => _directory.DeleteDirectory(recursive: true);

    private StagingPromoter NewPromoter(IFileHasher? hasher = null, IPromotionSink? sink = null)
        => new(_staging, sink ?? _sink, hasher ?? new XxHash3FileHasher(), NullLogger<StagingPromoter>.Instance);

    private AbsolutePath Stage(string path, string text)
    {
        var file = _staging.PathFor(RelativePath.FromUnsanitizedInput(path));
        file.Parent.CreateDirectory();
        File.WriteAllBytes(file.ToString(), Encoding.UTF8.GetBytes(text));
        return file;
    }

    private static Hash HashOf(string text) => Hash.From(System.IO.Hashing.XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public async Task PromoteStoresFilesRecordsThemAndEmptiesStaging()
    {
        Stage("Data/a.txt", "alpha");
        Stage("Data/sub/b.txt", "beta");
        _staging.WriteWhiteouts([RelativePath.FromUnsanitizedInput("Data/gone.txt")]);

        var result = await NewPromoter().Promote(Target);

        result.Status.Should().Be(PromotionStatus.Promoted);
        result.Files.Select(f => f.ToString()).Should().BeEquivalentTo("Data/a.txt", "Data/sub/b.txt");
        _sink.JournalExistedDuringBackup.Should().BeTrue("the journal has to be on disk before anything is stored");
        _sink.BackedUp.Should().BeEquivalentTo([HashOf("alpha"), HashOf("beta")]);
        var record = _sink.Records.Should().ContainSingle().Subject;
        record.Loadout.Should().Be(Target);
        record.Files.Select(f => (f.Path.ToString(), f.Hash, f.Size.Value)).Should().BeEquivalentTo([
            ("Data/a.txt", HashOf("alpha"), 5UL),
            ("Data/sub/b.txt", HashOf("beta"), 4UL),
        ]);
        record.Deleted.Select(d => d.ToString()).Should().BeEquivalentTo("Data/gone.txt");

        _staging.Scan().Should().BeEmpty();
        _staging.WhiteoutFile.FileExists.Should().BeFalse();
        _staging.JournalFile.FileExists.Should().BeFalse();
    }

    [Fact]
    public async Task TheJournalNamesEveryEntryWithItsHash()
    {
        Stage("Data/a.txt", "alpha");
        _staging.WriteWhiteouts([RelativePath.FromUnsanitizedInput("Data/gone.txt")]);
        string? journalText = null;
        _sink.OnBackup = () => journalText = File.ReadAllText(_staging.JournalFile.ToString());

        await NewPromoter().Promote(Target);

        var journal = JsonNode.Parse(journalText!)!;
        ((int)journal["version"]!).Should().Be(1);
        ((string)journal["installation"]!).Should().Be("abc");
        var entries = journal["entries"]!.AsArray();
        var file = entries.Single(e => (string)e!["kind"]! == "file")!;
        ((string)file["path"]!).Should().Be("Data/a.txt");
        ((int)file["size"]!).Should().Be(5);
        ((string)file["xxHash3"]!).Should().Be(((ulong)HashOf("alpha")).ToString("x16"));
        var whiteout = entries.Single(e => (string)e!["kind"]! == "whiteout")!;
        ((string)whiteout["path"]!).Should().Be("Data/gone.txt");
    }

    [Fact]
    public async Task NothingStagedMeansNothingToDo()
    {
        var result = await NewPromoter().Promote(Target);

        result.Status.Should().Be(PromotionStatus.NothingToDo);
        _sink.Calls.Should().BeEmpty();
        _staging.JournalFile.FileExists.Should().BeFalse();
    }

    [Fact]
    public async Task AFaultDuringBackupLeavesTheJournalAndStagingForRecover()
    {
        Stage("Data/a.txt", "alpha");
        Stage("Data/b.txt", "beta");
        _sink.FailAfterBackupEntries = 1;

        var act = () => NewPromoter().Promote(Target);
        await act.Should().ThrowAsync<InvalidOperationException>();

        _staging.JournalFile.FileExists.Should().BeTrue();
        _staging.Scan().Count(e => !e.IsDirectory).Should().Be(2);
        _sink.BackedUp.Should().HaveCount(1);
        _sink.Records.Should().BeEmpty();

        // A new promoter over the same directories, as after a restart.
        _sink.FailAfterBackupEntries = null;
        var recovered = await NewPromoter().Recover();

        recovered.Status.Should().Be(PromotionStatus.Recovered);
        _sink.BackedUp.Should().BeEquivalentTo([HashOf("alpha"), HashOf("beta")], "the entry stored before the fault is not stored twice");
        _sink.Records.Should().ContainSingle();
        _staging.Scan().Should().BeEmpty();
        _staging.JournalFile.FileExists.Should().BeFalse();

        _sink.Calls.Clear();
        var again = await NewPromoter().Recover();
        again.Status.Should().Be(PromotionStatus.NothingToDo);
        _sink.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task AFaultAfterRecordMakesRecoverRecordAgain()
    {
        Stage("Data/a.txt", "alpha");
        _staging.WriteWhiteouts([RelativePath.FromUnsanitizedInput("Data/gone.txt")]);
        _sink.FailAfterRecord = true;

        var act = () => NewPromoter().Promote(Target);
        await act.Should().ThrowAsync<InvalidOperationException>();
        _sink.Records.Should().HaveCount(1);
        _staging.JournalFile.FileExists.Should().BeTrue();

        _sink.FailAfterRecord = false;
        var recovered = await NewPromoter().Recover();

        recovered.Status.Should().Be(PromotionStatus.Recovered);
        _sink.Records.Should().HaveCount(2);
        _sink.Records[1].Should().BeEquivalentTo(_sink.Records[0]);
        _staging.JournalFile.FileExists.Should().BeFalse();
        _staging.Scan().Should().BeEmpty();
    }

    [Fact]
    public async Task RecoverOnlyFinishesTheCleanupOnceTheChangesAreRecorded()
    {
        // What a crash after Record, partway through deleting the staged files, leaves behind:
        // a journal marked as recorded, one file already deleted and one still there.
        Stage("Data/left.txt", "left behind");
        _staging.WriteWhiteouts([RelativePath.FromUnsanitizedInput("Data/gone.txt")]);
        new PromotionJournal(Installation, Target, [
            new JournalEntry(RelativePath.FromUnsanitizedInput("Data/deleted-already.txt"), JournalEntryKind.File, Size.From(3), HashOf("abc"), 0x1a4),
            new JournalEntry(RelativePath.FromUnsanitizedInput("Data/left.txt"), JournalEntryKind.File, Size.From(11), HashOf("left behind"), 0x1a4),
            new JournalEntry(RelativePath.FromUnsanitizedInput("Data/gone.txt"), JournalEntryKind.Whiteout, Size.Zero, default, 0),
        ], Recorded: true).Write(_staging.JournalFile);

        var recovered = await NewPromoter().Recover();

        recovered.Status.Should().Be(PromotionStatus.Recovered);
        _sink.Calls.Should().BeEmpty("everything was already stored and recorded");
        _staging.Scan().Should().BeEmpty();
        _staging.WhiteoutFile.FileExists.Should().BeFalse();
        _staging.JournalFile.FileExists.Should().BeFalse();
    }

    [Fact]
    public async Task PromoteMarksTheJournalRecordedOnceTheSinkHasTheChanges()
    {
        Stage("Data/a.txt", "alpha");
        _sink.FailAfterRecord = false;
        PromotionJournal? seen = null;
        _sink.OnRecord = () => seen = PromotionJournal.Read(_staging.JournalFile, Installation);

        await NewPromoter().Promote(Target);

        seen!.Recorded.Should().BeFalse("the marker is written after Record returns, not before");
        seen.Entries.Should().ContainSingle().Which.Hash.Should().Be(HashOf("alpha"));
    }

    [Fact]
    public async Task AFileChangedAfterJournalingBlocksPromotionBeforeAnythingIsStored()
    {
        var file = Stage("Data/a.txt", "alpha");
        Stage("Data/b.txt", "beta");
        var hasher = new RewritingHasher(file, "tampered");

        var result = await NewPromoter(hasher).Promote(Target);

        result.Status.Should().Be(PromotionStatus.Blocked);
        _sink.Calls.Should().BeEmpty("the store trusts the hash it is handed, so nothing may be stored before the check");
        result.RecoveryPath.Should().NotBeNull();
        var recovery = result.RecoveryPath!.Value;
        recovery.InFolder(_staging.RecoveryRoot).Should().BeTrue();
        File.ReadAllText(recovery.Combine("staged/Data/a.txt").ToString()).Should().Be("tampered");
        File.ReadAllText(recovery.Combine("staged/Data/b.txt").ToString()).Should().Be("beta");
        File.ReadAllText(recovery.Combine("README.txt").ToString()).Should().Contain("Data/a.txt");
        recovery.Combine("promotion.json").FileExists.Should().BeTrue();

        _staging.Scan().Should().BeEmpty();
        _staging.JournalFile.FileExists.Should().BeFalse();
        result.Files.Select(f => f.ToString()).Should().Contain("Data/a.txt");
    }

    [Fact]
    public async Task RecoverBlocksWhenAStagedFileChangedWhileTheAppWasDown()
    {
        var file = Stage("Data/a.txt", "alpha");
        _sink.FailAfterBackupEntries = 0;
        var act = () => NewPromoter().Promote(Target);
        await act.Should().ThrowAsync<InvalidOperationException>();
        File.WriteAllText(file.ToString(), "changed while down");
        _sink.Calls.Clear();

        var result = await NewPromoter().Recover();

        result.Status.Should().Be(PromotionStatus.Blocked);
        _sink.Calls.Should().BeEmpty();
        File.ReadAllText(result.RecoveryPath!.Value.Combine("staged/Data/a.txt").ToString()).Should().Be("changed while down");
    }

    [Fact]
    public async Task RecoverWithoutAJournalLeavesStagedFilesAlone()
    {
        Stage("Data/a.txt", "alpha");

        var result = await NewPromoter().Recover();

        result.Status.Should().Be(PromotionStatus.NothingToDo);
        _sink.Calls.Should().BeEmpty();
        _staging.Scan().Should().NotBeEmpty();
    }

    [Fact]
    public async Task PromoteRefusesToStartOverAnUnfinishedJournal()
    {
        Stage("Data/a.txt", "alpha");
        _sink.FailAfterBackupEntries = 0;
        var first = () => NewPromoter().Promote(Target);
        await first.Should().ThrowAsync<InvalidOperationException>();
        _sink.FailAfterBackupEntries = null;

        var second = () => NewPromoter().Promote(Target);

        await second.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Recover*");
    }

    [Fact]
    public async Task AJournalFromAnotherInstallationIsRefused()
    {
        Stage("Data/a.txt", "alpha");
        _sink.FailAfterBackupEntries = 0;
        var first = () => NewPromoter().Promote(Target);
        await first.Should().ThrowAsync<InvalidOperationException>();
        var text = File.ReadAllText(_staging.JournalFile.ToString()).Replace("\"abc\"", "\"def\"");
        File.WriteAllText(_staging.JournalFile.ToString(), text);

        var act = () => NewPromoter().Recover();

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    private sealed class RewritingHasher(AbsolutePath file, string newText) : IFileHasher
    {
        private readonly XxHash3FileHasher _real = new();
        private int _calls;

        public async ValueTask<Hash> Hash(GameInstallMetadataId installation, AbsolutePath path, CancellationToken token = default)
        {
            // First pass hashes the file as it was staged; the verify pass finds it rewritten.
            if (path == file && _calls++ == 1) File.WriteAllText(file.ToString(), newText);
            return await _real.Hash(installation, path, token);
        }
    }

    private sealed record RecordCall(LoadoutId Loadout, IReadOnlyList<PromotedFile> Files, IReadOnlyList<RelativePath> Deleted);

    private sealed class FaultingSink(StagingDirectory staging) : IPromotionSink
    {
        private readonly HashSet<Hash> _known = [];

        public List<string> Calls { get; } = [];
        public List<Hash> BackedUp { get; } = [];
        public List<RecordCall> Records { get; } = [];
        public bool JournalExistedDuringBackup { get; private set; }
        public int? FailAfterBackupEntries { get; set; }
        public bool FailAfterRecord { get; set; }
        public Action? OnBackup { get; set; }
        public Action? OnRecord { get; set; }

        public Task Backup(IReadOnlyList<(Hash Hash, Size Size, IStreamFactory Stream)> files, CancellationToken token = default)
        {
            Calls.Add("backup");
            JournalExistedDuringBackup = staging.JournalFile.FileExists;
            OnBackup?.Invoke();

            var stored = 0;
            foreach (var (hash, _, _) in files)
            {
                // A store that already has the hash skips it, as the real sink does.
                if (_known.Contains(hash)) continue;
                if (stored++ >= FailAfterBackupEntries) throw new InvalidOperationException("injected fault during backup");
                _known.Add(hash);
                BackedUp.Add(hash);
            }
            return Task.CompletedTask;
        }

        public Task Record(LoadoutId loadout, IReadOnlyList<PromotedFile> files, IReadOnlyList<RelativePath> deleted, CancellationToken token = default)
        {
            Calls.Add("record");
            OnRecord?.Invoke();
            Records.Add(new RecordCall(loadout, files, deleted));
            if (FailAfterRecord) throw new InvalidOperationException("injected fault after record");
            return Task.CompletedTask;
        }
    }
}
