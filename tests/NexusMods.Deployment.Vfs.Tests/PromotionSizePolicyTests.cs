using System.Diagnostics;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.IO;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Deployment.Vfs.Tests;

/// <summary>
/// The promotion size cap, against staged files created sparse: their length is gigabytes but they
/// occupy no blocks, so hashing them (which the cap must prevent) would be the only cost.
/// </summary>
public class PromotionSizePolicyTests : IDisposable
{
    private const long GiB = 1024L * 1024 * 1024;
    private static readonly LoadoutId Target = LoadoutId.From(0x77);
    private static readonly GameInstallMetadataId Installation = GameInstallMetadataId.From(0xABC);
    private static readonly Size Cap = Size.From(16UL * GiB);

    private readonly AbsolutePath _directory = FileSystem.Shared
        .GetKnownPath(KnownPath.TempDirectory)
        .Combine($"vfs-size-policy-tests-{Guid.NewGuid():N}");

    private readonly StagingDirectory _staging;
    private readonly RecordingSink _sink = new();
    private readonly CountingHasher _hasher = new();

    public PromotionSizePolicyTests()
    {
        _staging = new StagingDirectory(_directory.Combine("staging"), Installation);
    }

    public void Dispose() => _directory.DeleteDirectory(recursive: true);

    private StagingPromoter NewPromoter() => new(_staging, _sink, _hasher, NullLogger<StagingPromoter>.Instance, Cap);

    private AbsolutePath StageSparse(string path, long length)
    {
        var file = _staging.PathFor(RelativePath.FromUnsanitizedInput(path));
        file.Parent.CreateDirectory();
        using var stream = File.Create(file.ToString());
        stream.SetLength(length);
        return file;
    }

    private AbsolutePath Stage(string path, string text)
    {
        var file = _staging.PathFor(RelativePath.FromUnsanitizedInput(path));
        file.Parent.CreateDirectory();
        File.WriteAllBytes(file.ToString(), Encoding.UTF8.GetBytes(text));
        return file;
    }

    private void StageOverCap()
    {
        var first = StageSparse("Data/a.bin", 6 * GiB);
        StageSparse("Data/b.bin", 6 * GiB);
        StageSparse("Data/c.bin", 6 * GiB);
        Stage("Data/small.txt", new string('x', 1024));

        Assert.SkipWhen(new FileInfo(first.ToString()).Length != 6 * GiB, "the temp filesystem does not report sparse file lengths");
        Assert.SkipWhen(DiskUsageKiB(_directory) > 1024, "the temp filesystem does not support sparse files");
    }

    private static long DiskUsageKiB(AbsolutePath path)
    {
        using var du = Process.Start(new ProcessStartInfo("du", ["-sk", path.ToString()]) { RedirectStandardOutput = true })!;
        var output = du.StandardOutput.ReadToEnd();
        du.WaitForExit();
        return long.Parse(output.Split('\t')[0]);
    }

    [Fact]
    public async Task OverTheCapPromoteRefusesBeforeHashingAnything()
    {
        StageOverCap();

        var report = StagingReport.Build(_staging);
        var verdict = PromotionSizePolicy.Evaluate(report, Cap).Should().BeOfType<SizeVerdict.OverCap>().Subject;
        verdict.Total.Value.Should().Be(18UL * (ulong)GiB + 1024);
        verdict.Cap.Should().Be(Cap);
        verdict.Largest.Select(f => f.Path.ToString()).Should().Equal("Data/a.bin", "Data/b.bin", "Data/c.bin", "Data/small.txt");

        var result = await NewPromoter().Promote(Target);

        result.Status.Should().Be(PromotionStatus.OverCap);
        result.Verdict.Should().BeEquivalentTo(verdict);
        _hasher.Calls.Should().Be(0, "the size check has to run before any file is read");
        _sink.Calls.Should().BeEmpty();
        _staging.JournalFile.FileExists.Should().BeFalse();
        _staging.Scan().Count(e => !e.IsDirectory).Should().Be(4, "an over-cap promote touches nothing");
    }

    [Fact]
    public void TheVerdictNamesAtMostTheTenLargestFiles()
    {
        for (var i = 0; i < 12; i++) StageSparse($"Data/f{i:D2}.bin", (i + 1) * GiB);

        var verdict = PromotionSizePolicy.Evaluate(StagingReport.Build(_staging), Size.From(1)).Should().BeOfType<SizeVerdict.OverCap>().Subject;

        verdict.Largest.Should().HaveCount(10);
        verdict.Largest[0].Path.ToString().Should().Be("Data/f11.bin");
        verdict.Largest[9].Path.ToString().Should().Be("Data/f02.bin");
    }

    [Fact]
    public async Task DiscardingTheLargestFileBringsStagingUnderTheCap()
    {
        StageOverCap();
        var promoter = NewPromoter();

        promoter.Discard(RelativePath.FromUnsanitizedInput("Data/a.bin"));

        _staging.PathFor(RelativePath.FromUnsanitizedInput("Data/a.bin")).FileExists.Should().BeFalse();
        _staging.PathFor(RelativePath.FromUnsanitizedInput("Data/b.bin")).FileExists.Should().BeTrue("discard removes only the named file");
        PromotionSizePolicy.Evaluate(StagingReport.Build(_staging), Cap).Should().Be(SizeVerdict.Ok);
        (await promoter.Promote(Target)).Status.Should().Be(PromotionStatus.Promoted);
    }

    [Fact]
    public void DiscardOfAFileThatIsNotStagedThrows()
    {
        var act = () => NewPromoter().Discard(RelativePath.FromUnsanitizedInput("Data/missing.bin"));
        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void DiscardLeavesTheWhiteoutsAlone()
    {
        Stage("Data/a.txt", "alpha");
        _staging.WriteWhiteouts([RelativePath.FromUnsanitizedInput("Data/gone.txt")]);

        NewPromoter().Discard(RelativePath.FromUnsanitizedInput("Data/a.txt"));

        _staging.ReadWhiteouts().Select(w => w.ToString()).Should().Equal("Data/gone.txt");
    }

    [Fact]
    public void DiscardAllEmptiesStagingAndWritesNoJournal()
    {
        Stage("Data/a.txt", "alpha");
        _staging.WriteWhiteouts([RelativePath.FromUnsanitizedInput("Data/gone.txt")]);

        NewPromoter().DiscardAll();

        _staging.Scan().Should().BeEmpty();
        _staging.ReadWhiteouts().Should().BeEmpty();
        _staging.JournalFile.FileExists.Should().BeFalse();
        _sink.Calls.Should().BeEmpty();
    }

    [Fact]
    public void DiscardRefusesWhileAnInterruptedPromotionIsWaiting()
    {
        Stage("Data/a.txt", "alpha");
        File.WriteAllText(_staging.JournalFile.ToString(), "{}");
        var promoter = NewPromoter();

        var discard = () => promoter.Discard(RelativePath.FromUnsanitizedInput("Data/a.txt"));
        var discardAll = () => promoter.DiscardAll();

        discard.Should().Throw<InvalidOperationException>();
        discardAll.Should().Throw<InvalidOperationException>();
        _staging.Scan().Should().NotBeEmpty();
    }

    [Fact]
    public async Task FinishSessionWithDiscardClearsStagingAndNeverCallsTheSink()
    {
        Stage("Data/a.txt", "alpha");
        var session = NewSession();
        session.DiscardStagingOnUnmount = true;

        var result = await session.FinishSession();

        result.Status.Should().Be(PromotionStatus.NothingToDo);
        _staging.Scan().Should().BeEmpty();
        _sink.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task FinishSessionPromotesByDefault()
    {
        Stage("Data/a.txt", "alpha");
        var session = NewSession();
        session.DiscardStagingOnUnmount.Should().BeFalse();

        var result = await session.FinishSession();

        result.Status.Should().Be(PromotionStatus.Promoted);
        _sink.Calls.Should().Equal("backup", "record");
        _staging.Scan().Should().BeEmpty();
    }

    private VfsSession NewSession()
    {
        var tree = VfsTree.Build(_directory.Combine("game"), [], [], _staging, VfsCachePolicy.Default);
        return new VfsSession(tree, _staging, NewPromoter(), Target);
    }

    // Hashes from the length alone: reading gigabytes of sparse zeros on the retry after a discard
    // would cost seconds and prove nothing about the size policy.
    private sealed class CountingHasher : IFileHasher
    {
        public int Calls { get; private set; }

        public ValueTask<Hash> Hash(GameInstallMetadataId installation, AbsolutePath file, CancellationToken token = default)
        {
            Calls++;
            return ValueTask.FromResult(NexusMods.Hashing.xxHash3.Hash.From((ulong)new FileInfo(file.ToString()).Length));
        }
    }

    private sealed class RecordingSink : IPromotionSink
    {
        public List<string> Calls { get; } = [];

        public Task Backup(IReadOnlyList<(Hash Hash, Size Size, IStreamFactory Stream)> files, CancellationToken token = default)
        {
            Calls.Add("backup");
            return Task.CompletedTask;
        }

        public Task Record(LoadoutId loadout, IReadOnlyList<PromotedFile> files, IReadOnlyList<RelativePath> deleted, CancellationToken token = default)
        {
            Calls.Add("record");
            return Task.CompletedTask;
        }
    }
}
