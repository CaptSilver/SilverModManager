using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.GC;
using NexusMods.Abstractions.Loadouts.Deployment;
using NexusMods.Abstractions.Loadouts.Synchronizers.Deployment;
using NexusMods.Sdk.Settings;
using NexusMods.App.GarbageCollection.DataModel;
using NexusMods.CrossPlatform;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;

namespace NexusMods.DataModel;

/// <inheritdoc />
public class GarbageCollectorRunner(ISettingsManager settings, NxFileStore store, IConnection connection, ILooseStagingStore stagingStore, IDeploymentPlanStore plans, IEnumerable<IDeploymentBackend> backends, IGameRegistry gameRegistry, ILogger<GarbageCollectorRunner> logger) : IGarbageCollectorRunner
{
    private readonly DataModelSettings _settings = settings.Get<DataModelSettings>();
    private readonly NxFileStore _store = store;
    private readonly IConnection _connection = connection;
    private readonly ILogger<GarbageCollectorRunner> _logger = logger;
    private readonly ILooseStagingStore _stagingStore = stagingStore;
    private readonly IDeploymentPlanStore _plans = plans;
    private readonly IReadOnlySet<DeploymentBackendId> _stagingBackends = backends.Where(b => b.UsesLooseStaging).Select(b => b.Id).ToHashSet();
    private readonly IReadOnlyDictionary<DeploymentBackendId, IDeploymentBackend> _backends = backends.ToDictionary(b => b.Id);

    /// <inheritdoc />
    public void Run()
    {
        RunGarbageCollector.Do(logger, _settings.ArchiveLocations, _store, _connection, _stagingStore, _plans, _stagingBackends, StoreLookup());
    }

    // Asks the backend that deploys a folder which store its files link from, at the folder's location as the
    // game registry reports it now. The registry is read once per run, on the first folder that needs it.
    private StagingStoreLookup StoreLookup()
    {
        var installations = new Lazy<Dictionary<GameInstallMetadataId, GameInstallation>>(() =>
        {
            var byId = new Dictionary<GameInstallMetadataId, GameInstallation>();
            foreach (var installation in gameRegistry.LocateGameInstallations())
            {
                if (gameRegistry.TryGetMetadata(installation, out var metadata)) byId[metadata.GameInstallMetadataId] = installation;
            }
            return byId;
        });

        return (id, backendId, folder) =>
        {
            if (!_backends.TryGetValue(backendId, out var backend)) return null;
            if (!installations.Value.TryGetValue(id, out var installation)) return null;
            if (!installation.Locations.TryGetValue(folder.LocationId, out var location)) return null;
            return backend.StagingStoreFor(_stagingStore, location.Path.Combine(folder.Path));
        };
    }
    
    /// <inheritdoc />
    public Task RunAsync()
    {
        return Task.Run(Run);
    }
    
    /// <inheritdoc />
    public async Task RunWithMode(GarbageCollectorRunMode gcRunMode)
    {
        switch (gcRunMode)
        {
            case GarbageCollectorRunMode.RunSynchronously:
                Run();
                break;
            case GarbageCollectorRunMode.RunAsyncInBackground:
                RunAsync().FireAndForget(_logger);
                break;
            case GarbageCollectorRunMode.RunAsynchronously:
                await RunAsync();
                break;
            case GarbageCollectorRunMode.DoNotRun:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(gcRunMode), gcRunMode, null);
        }
    }
}
