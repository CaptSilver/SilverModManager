using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.DataModel;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Settings;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;
using System.Text;

namespace SilverModManager.App.GarbageCollection.DataModel.Tests;

/// <summary>
/// A job that backs bytes up, records the row that owns them and drops its pin can finish while a collection is
/// marking. The collection must keep the bytes whichever side of the commit and the unpin its snapshot of the
/// database falls on.
/// </summary>
public class PinnedBackupCollectionRaceTests(IServiceProvider serviceProvider, ISettingsManager settingsManager) : AGameTest<StubbedGame>(serviceProvider)
{
    private readonly NxFileStore _fileStore = serviceProvider.GetRequiredService<NxFileStore>();
    private readonly DataModelSettings _dataModelSettings = settingsManager.Get<DataModelSettings>();
    private readonly ILooseStagingStore _stagingStore = serviceProvider.GetRequiredService<ILooseStagingStore>();
    private readonly IDeploymentPlanStore _plans = serviceProvider.GetRequiredService<IDeploymentPlanStore>();
    private readonly ILogger _logger = serviceProvider.GetRequiredService<ILogger<PinnedBackupCollectionRaceTests>>();

    [Fact]
    public async Task ABackupCommittedAndUnpinnedAfterTheMarkTookItsSnapshotSurvivesTheCollection()
    {
        var text = $"baseline reset {Guid.NewGuid()}";
        var bytes = Encoding.UTF8.GetBytes(text);
        var identity = FileIdentity.XxHash3(bytes.xxHash3());
        var pin = FileStorePins.Pin(identity);
        await _fileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(text, new MemoryStream(bytes)), identity, Size.FromLong(bytes.Length))]);

        // The job finishes at the one point that decides the outcome: right after the mark has read the database
        // and before it has looked at the pins.
        var finishedJob = false;
        var connection = Substitute.For<IConnection>();
        connection.Db.Returns(call =>
        {
            var snapshot = Connection.Db;
            if (finishedJob) return snapshot;
            finishedJob = true;
            using var tx = Connection.BeginTransaction();
            _ = new LibraryFile.New(tx, out var id)
            {
                FileName = "Baseline.ini",
                Hash = identity.XxHash3(),
                Size = Size.FromLong(bytes.Length),
                LibraryItem = new LibraryItem.New(tx, id) { Name = "Baseline.ini" },
            };
            tx.Commit().GetAwaiter().GetResult();
            pin.Dispose();
            return snapshot;
        });

        RunGarbageCollector.Do(_logger, _dataModelSettings.ArchiveLocations, _fileStore, connection, _stagingStore, _plans, new HashSet<DeploymentBackendId>());

        finishedJob.Should().BeTrue("the collection read the database");
        (await _fileStore.Load(identity)).Should().Equal(bytes);
    }

    [Fact]
    public async Task AJobThatPinsOrphanedBytesAndSkipsTheBackupAfterTheMarkReadThePinsKeepsThem()
    {
        var text = $"orphaned baseline {Guid.NewGuid()}";
        var bytes = Encoding.UTF8.GetBytes(text);
        var identity = FileIdentity.XxHash3(bytes.xxHash3());

        // Stored earlier by a job that has long since finished and whose row is gone: nothing references it.
        await _fileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(text, new MemoryStream(bytes)), identity, Size.FromLong(bytes.Length))]);

        // The job starts after the mark read the pins: it pins, sees the bytes (HaveFile takes no lock), skips
        // BackupFiles and so never meets the collector's write lock, commits its row and unpins.
        var jobRan = false;
        var skippedBackup = false;
        var connection = Substitute.For<IConnection>();
        connection.Db.Returns(call =>
        {
            var snapshot = Connection.Db;
            if (jobRan) return snapshot;
            jobRan = true;
            var pin = FileStorePins.Pin(identity);
            skippedBackup = _fileStore.HaveFile(identity).AsTask().GetAwaiter().GetResult();
            using var tx = Connection.BeginTransaction();
            _ = new LibraryFile.New(tx, out var id)
            {
                FileName = "Orphan.ini",
                Hash = identity.XxHash3(),
                Size = Size.FromLong(bytes.Length),
                LibraryItem = new LibraryItem.New(tx, id) { Name = "Orphan.ini" },
            };
            tx.Commit().GetAwaiter().GetResult();
            pin.Dispose();
            return snapshot;
        });

        RunGarbageCollector.Do(_logger, _dataModelSettings.ArchiveLocations, _fileStore, connection, _stagingStore, _plans, new HashSet<DeploymentBackendId>());

        jobRan.Should().BeTrue("the collection read the database");
        skippedBackup.Should().BeTrue("the bytes were already stored, so the job had nothing to back up");
        (await _fileStore.Load(identity)).Should().Equal(bytes);
    }
}
