using FluentAssertions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Install;
using WriteState = SilverModManager.Wabbajack.Install.InstallModlistJob.WriteState;

namespace SilverModManager.Wabbajack.Tests.Install;

public class WriteBatchTests(ITestOutputHelper helper) : InstallTestBase<WriteBatchTests>(helper)
{
    private static readonly CommitPolicy Roomy = new(MaxItems: 1000, MaxBytes: long.MaxValue, MaxInterval: TimeSpan.FromHours(1));

    private WriteBatch Batch(CommitPolicy policy, IFileStore? store = null)
        => new(Connection, policy, new GeneratedFileWriter(store ?? FileStore, TemporaryFileManager));

    private async Task Generated(WriteBatch batch, byte[] bytes)
    {
        var file = await new GeneratedFileWriter(FileStore, TemporaryFileManager).Generate(async o => await o.WriteAsync(bytes), CancellationToken.None);
        batch.AddPending(file);
    }

    [Fact]
    public void IsDue_OnceItemsReachTheLimit()
    {
        var batch = Batch(Roomy with { MaxItems = 3 });
        batch.Open().Dispose();

        batch.Items = 2;
        batch.IsDue.Should().BeFalse();
        batch.Items = 3;
        batch.IsDue.Should().BeTrue();
    }

    [Fact]
    public void IsDue_OnceBytesReachTheLimit()
    {
        var batch = Batch(Roomy with { MaxBytes = 100 });
        batch.Open().Dispose();

        batch.Bytes = 99;
        batch.IsDue.Should().BeFalse();
        batch.Bytes = 100;
        batch.IsDue.Should().BeTrue();
    }

    [Fact]
    public void IsDue_WithAZeroInterval_RightAfterOpening()
    {
        var batch = Batch(Roomy with { MaxInterval = TimeSpan.Zero });
        batch.Open().Dispose();

        batch.IsDue.Should().BeTrue();
    }

    [Fact]
    public async Task Commit_RemapsNewGroupsToTheirCommittedIds()
    {
        var loadout = await CreateLoadout();
        var batch = Batch(Roomy);
        var state = new WriteState();
        var tx = batch.Open();
        var created = new LoadoutItemGroup.New(tx, out var id)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, id) { LoadoutId = loadout.LoadoutId, Name = "Mod A" },
        };
        state.Groups["A"] = created.LoadoutItemGroupId;
        state.NewGroups.Add("A");
        batch.Dirty = true;

        await batch.Commit(state, CancellationToken.None);

        state.NewGroups.Should().BeEmpty();
        LoadoutItem.Load(Connection.Db, state.Groups["A"].Value).Name.Should().Be("Mod A");
        batch.Items.Should().Be(0);
        batch.Dirty.Should().BeFalse();
    }

    [Fact]
    public async Task Commit_WithNothingWritten_CommitsNoTransaction()
    {
        var batch = Batch(Roomy);
        var before = Connection.Db.BasisTxId;

        await batch.Commit(new WriteState(), CancellationToken.None);
        batch.Open();
        batch.Items = 1;
        await batch.Commit(new WriteState(), CancellationToken.None);

        Connection.Db.BasisTxId.Should().Be(before, "a batch whose items wrote no rows has nothing to commit");
    }

    [Fact]
    public async Task Flush_AfterAFailedFlush_RethrowsThatFailure()
    {
        var batch = Batch(Roomy, new FailingStore(FileStore));
        await Generated(batch, [1, 2, 3]);

        var first = () => batch.Flush(CancellationToken.None);
        await first.Should().ThrowAsync<IOException>().WithMessage("store is full");

        // The failed flush took the waiting files; without the remembered failure the next flush would find nothing and pass.
        var second = () => batch.Flush(CancellationToken.None);
        await second.Should().ThrowAsync<IOException>().WithMessage("store is full");
    }

    private sealed class FailingStore(IFileStore inner) : ForwardingFileStore(inner)
    {
        public override Task BackupFiles(IEnumerable<ArchivedFileEntry> backups, bool deduplicate = true, CancellationToken token = default, IProgress<double>? progress = null)
            => throw new IOException("store is full");
    }
}
