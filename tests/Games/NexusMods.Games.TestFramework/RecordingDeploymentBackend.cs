using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Deployment;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Abstractions.Loadouts.Synchronizers.Deployment;
using NexusMods.Abstractions.Loadouts.Synchronizers.Rules;
using NexusMods.MnemonicDB.Abstractions.IndexSegments;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.TestFramework;

/// <summary>
/// Backend that notes which paths each phase was asked to handle, then lets the copy backend do the work so
/// disk and database stay consistent.
/// </summary>
/// <remarks>
/// Only nodes whose actions carry the phase's flag are recorded, so an assertion on <see cref="Calls"/> says
/// what the phase would really have touched rather than everything routed here.
/// </remarks>
public sealed class RecordingDeploymentBackend(CopyDeploymentBackend inner) : IDeploymentBackend
{
    public static readonly DeploymentBackendId BackendId = new("test-recording");

    public DeploymentBackendId Id => BackendId;

    public bool UsesLooseStaging => false;

    public DeploymentPreflight Preflight(GameInstallation installation, GamePath root)
    {
        Calls.Add((nameof(Preflight), root));
        return DeploymentPreflight.Accept("records the call");
    }

    public IReadOnlyList<AbsolutePath> RequiredFilesystems(GameInstallation installation, GamePath root) => [];

    public List<(string Phase, GamePath Path)> Calls { get; } = [];

    private void Record(string phase, Actions flag, IReadOnlyDictionary<GamePath, SyncNode> nodes)
    {
        foreach (var (path, node) in nodes)
        {
            if (node.Actions.HasFlag(flag)) Calls.Add((phase, path));
        }
    }

    public Task Backup(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes)
    {
        Record(nameof(Backup), Actions.BackupFile, nodes);
        return inner.Backup(context, nodes);
    }

    public void Ingest(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes)
    {
        Record(nameof(Ingest), Actions.IngestFromDisk, nodes);
        inner.Ingest(context, nodes);
    }

    public Task AdaptLoadout(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes)
    {
        Record(nameof(AdaptLoadout), Actions.AdaptLoadout, nodes);
        return inner.AdaptLoadout(context, nodes);
    }

    public IReadOnlyCollection<GamePath> Delete(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes)
    {
        Record(nameof(Delete), Actions.DeleteFromDisk, nodes);
        return inner.Delete(context, nodes);
    }

    public Task Extract(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes)
    {
        Record(nameof(Extract), Actions.ExtractToDisk, nodes);
        return inner.Extract(context, nodes);
    }

    public Task WriteIntrinsics(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes)
    {
        Record(nameof(WriteIntrinsics), Actions.WriteIntrinsic, nodes);
        return inner.WriteIntrinsics(context, nodes);
    }

    public void AddReifiedDeletes(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes)
    {
        Record(nameof(AddReifiedDeletes), Actions.AddReifiedDelete, nodes);
        inner.AddReifiedDeletes(context, nodes);
    }

    public void CleanDirectories(DeploymentContext context, IReadOnlyCollection<GamePath> directoriesWithDeletions, Entities<DiskStateEntry.ReadOnly> newDiskState)
    {
        foreach (var directory in directoriesWithDeletions) Calls.Add((nameof(CleanDirectories), directory));
        inner.CleanDirectories(context, directoriesWithDeletions, newDiskState);
    }
}

public static class RecordingDeploymentBackendServices
{
    /// <summary>
    /// Registers <see cref="RecordingDeploymentBackend"/> next to the copy backend.
    /// </summary>
    public static IServiceCollection AddRecordingDeploymentBackend(this IServiceCollection services)
    {
        services.TryAddSingleton<RecordingDeploymentBackend>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDeploymentBackend, RecordingDeploymentBackend>(sp => sp.GetRequiredService<RecordingDeploymentBackend>()));
        return services;
    }
}
