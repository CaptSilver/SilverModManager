using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.GC;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Deployment;
using NexusMods.DataModel;
using NexusMods.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.IO;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Settings;
using NexusMods.StandardGameLocators.TestHelpers.StubbedGames;

namespace NexusMods.App.GarbageCollection.DataModel.Tests.FindUsedFiles;

public class StagingLivenessTests(IServiceProvider serviceProvider, ISettingsManager settingsManager) : AGameTest<StubbedGame>(serviceProvider)
{
    // Deleting a loadout resets the game through the plan, so the backend has to be registered. Its
    // UsesLooseStaging is false; the id is passed to the collector explicitly instead.
    private static readonly DeploymentBackendId StagingBackend = RecordingDeploymentBackend.BackendId;

    private readonly NxFileStore _fileStore = serviceProvider.GetRequiredService<NxFileStore>();
    private readonly ILooseStagingStore _staging = serviceProvider.GetRequiredService<ILooseStagingStore>();
    private readonly IDeploymentPlanStore _plans = serviceProvider.GetRequiredService<IDeploymentPlanStore>();
    private readonly DataModelSettings _dataModelSettings = settingsManager.Get<DataModelSettings>();
    private readonly ILogger _logger = serviceProvider.GetRequiredService<ILogger<StagingLivenessTests>>();

    [Fact]
    public async Task SweepKeepsFilesUnderStagingRootsOfValidLoadoutsOnly()
    {
        var loadout = await CreateLoadout();
        var installation = loadout.InstallationId;

        using (var tx = Connection.BeginTransaction())
        {
            await AddModAsync(tx, ["mods/x"], loadout, "Mods");
            await AddModAsync(tx, ["other/y"], loadout, "Other");
            _plans.Write(tx, installation, new GamePath(LocationId.Game, "mods"), StagingBackend);
            await tx.Commit();
        }

        var modsHash = Hash("mods/x");
        var otherHash = Hash("other/y");
        var orphanHash = Hash("orphan");
        await _fileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory("orphan", new MemoryStream(Encoding.UTF8.GetBytes("orphan"))), orphanHash, Size.FromLong(6))]);

        var otherInstallation = GameInstallMetadataId.From(EntityId.From(0xABCDEF));
        await _staging.Ensure(installation, modsHash);
        await _staging.Ensure(installation, otherHash);
        await _staging.Ensure(installation, orphanHash);
        await _staging.Ensure(otherInstallation, modsHash);

        RunGc();

        _staging.Contains(installation, modsHash).Should().BeTrue("it sits under a root routed to a staging backend");
        _staging.Contains(installation, otherHash).Should().BeFalse("its root is routed to the copy backend");
        _staging.Contains(installation, orphanHash).Should().BeFalse("no loadout holds it");
        _staging.Installations().Should().NotContain(otherInstallation, "that installation routes nothing to a staging backend");

        await DeleteLoadoutAsync(loadout.Id, GarbageCollectorRunMode.DoNotRun);
        RunGc();

        _staging.Contains(installation, modsHash).Should().BeFalse("the loadout that held it is gone");
    }

    [Fact]
    public async Task SweepKeepsFilesRecordedInDiskStateUnderStagingRoots()
    {
        var loadout = await CreateLoadout();
        var installation = loadout.InstallationId;
        var deployed = Hash("deployed");
        var elsewhere = Hash("elsewhere");

        using (var tx = Connection.BeginTransaction())
        {
            _plans.Write(tx, installation, new GamePath(LocationId.Game, "mods"), StagingBackend);
            AddDiskState(tx, installation, new GamePath(LocationId.Game, "mods/deployed"), deployed);
            AddDiskState(tx, installation, new GamePath(LocationId.Game, "other/elsewhere"), elsewhere);
            await tx.Commit();
        }
        await _fileStore.BackupFiles([
            new ArchivedFileEntry(new MemoryStreamFactory("deployed", new MemoryStream(Encoding.UTF8.GetBytes("deployed"))), deployed, Size.FromLong(8)),
            new ArchivedFileEntry(new MemoryStreamFactory("elsewhere", new MemoryStream(Encoding.UTF8.GetBytes("elsewhere"))), elsewhere, Size.FromLong(9)),
        ]);
        await _staging.Ensure(installation, deployed);
        await _staging.Ensure(installation, elsewhere);

        RunGc();

        _staging.Contains(installation, deployed).Should().BeTrue("a deployed link would dangle without it");
        _staging.Contains(installation, elsewhere).Should().BeFalse("its path is routed to the copy backend");
    }

    private static void AddDiskState(ITransaction tx, GameInstallMetadataId installation, GamePath path, Hash hash)
    {
        _ = new DiskStateEntry.New(tx, tx.TempId(DiskStateEntry.EntryPartition))
        {
            Path = path.ToGamePathParentTuple(installation),
            Hash = hash,
            Size = Size.FromLong(1),
            LastModified = DateTimeOffset.UnixEpoch.UtcDateTime,
            GameId = installation,
        };
    }

    private void RunGc() => RunGarbageCollector.Do(_logger, _dataModelSettings.ArchiveLocations, _fileStore, Connection, _staging, _plans, new HashSet<DeploymentBackendId> { StagingBackend });

    private static Hash Hash(string content) => Encoding.UTF8.GetBytes(content).xxHash3();

    public class Startup
    {
        public void ConfigureServices(IServiceCollection services) => DIHelpers.ConfigureServices(services);
    }
}
