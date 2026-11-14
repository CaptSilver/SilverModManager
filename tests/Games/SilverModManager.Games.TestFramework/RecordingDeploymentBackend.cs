using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.IndexSegments;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.TestFramework;

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

    public Task StampTimestamps(DeploymentContext context, IReadOnlyList<(GamePath Path, DateTimeOffset Mtime)> stamps)
    {
        foreach (var (path, _) in stamps) Calls.Add((nameof(StampTimestamps), path));
        return inner.StampTimestamps(context, stamps);
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

/// <summary>
/// A mounting backend that serves a manifest the test sets and writes nothing for <c>Delete</c> and <c>Extract</c>, the
/// way a mount backend records rows instead of files. The other phases are the copy backend's.
/// </summary>
public sealed class RecordingMountingDeploymentBackend(CopyDeploymentBackend inner) : IMountingDeploymentBackend
{
    public static readonly DeploymentBackendId BackendId = new("test-mounting");

    public DeploymentBackendId Id => BackendId;

    public bool UsesLooseStaging => false;

    /// <summary>What <see cref="Manifest"/> serves, by root. A root with no entry has an empty manifest.</summary>
    public Dictionary<GamePath, MountManifestView> Views { get; } = [];

    public bool Mounted { get; set; }

    /// <summary>Roots whose <see cref="Mount"/> throws, to test what a scope does with the mounts already up.</summary>
    public HashSet<GamePath> FailingMounts { get; } = [];

    /// <summary>Runs inside a mount's <c>Finish</c>, where a real mount promotes what the game wrote.</summary>
    public Func<GamePath, Task>? OnFinish { get; set; }

    /// <summary>What a mount's <c>Finish</c> returns by root. A root with no entry finishes with nothing to do.</summary>
    public Dictionary<GamePath, MountFinishResult> FinishResults { get; } = [];

    public List<(string Phase, GamePath Path)> Calls { get; } = [];

    /// <summary>When set, <see cref="Preflight"/> refuses every root with this reason.</summary>
    public string? Refusal { get; set; }

    public DeploymentPreflight Preflight(GameInstallation installation, GamePath root) =>
        Refusal is null ? DeploymentPreflight.Accept("records the call") : DeploymentPreflight.Refuse(Refusal);

    public IReadOnlyList<AbsolutePath> RequiredFilesystems(GameInstallation installation, GamePath root) => [];

    public MountManifestView Manifest(GameInstallMetadataId installation, GamePath root, IDb db) =>
        Views.TryGetValue(root, out var view) ? view : MountManifestView.Empty(root);

    public bool IsMounted(GameInstallation installation, GamePath root) => Mounted;

    public Task<IDeploymentMount> Mount(GameInstallation installation, Loadout.ReadOnly loadout, GamePath root, CancellationToken token)
    {
        Calls.Add((nameof(Mount), root));
        if (FailingMounts.Contains(root)) throw new InvalidOperationException($"mounting {root} failed");
        return Task.FromResult<IDeploymentMount>(new RecordedMount(this, root, installation.Locations.ToAbsolutePath(root)));
    }

    public Task<MountRecoveryReport> Recover(GameInstallation installation, GamePath root, LoadoutId? target, CancellationToken token)
    {
        Calls.Add((nameof(Recover), root));
        return Task.FromResult(new MountRecoveryReport(MountRecoveryStatus.Nothing, WasMounted: false));
    }

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

    public Task AdaptLoadout(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => inner.AdaptLoadout(context, nodes);

    public IReadOnlyCollection<GamePath> Delete(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes)
    {
        Record(nameof(Delete), Actions.DeleteFromDisk, nodes);
        return [];
    }

    public Task Extract(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes)
    {
        Record(nameof(Extract), Actions.ExtractToDisk, nodes);
        return Task.CompletedTask;
    }

    public Task WriteIntrinsics(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => inner.WriteIntrinsics(context, nodes);

    public void AddReifiedDeletes(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => inner.AddReifiedDeletes(context, nodes);

    public void CleanDirectories(DeploymentContext context, IReadOnlyCollection<GamePath> directoriesWithDeletions, Entities<DiskStateEntry.ReadOnly> newDiskState)
    {
    }

    private sealed class RecordedMount(RecordingMountingDeploymentBackend backend, GamePath root, AbsolutePath mountPoint) : IDeploymentMount
    {
        public GamePath Root => root;

        public AbsolutePath MountPoint => mountPoint;

        public async Task<MountFinishResult> Finish(CancellationToken token)
        {
            backend.Calls.Add((nameof(Finish), root));
            if (backend.OnFinish is { } onFinish) await onFinish(root);
            return backend.FinishResults.TryGetValue(root, out var result) ? result : new MountFinishResult(MountFinishStatus.NothingToDo, []);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public static class RecordingMountingDeploymentBackendServices
{
    /// <summary>
    /// Registers <see cref="RecordingMountingDeploymentBackend"/> next to the copy backend.
    /// </summary>
    public static IServiceCollection AddRecordingMountingDeploymentBackend(this IServiceCollection services)
    {
        services.TryAddSingleton<RecordingMountingDeploymentBackend>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDeploymentBackend, RecordingMountingDeploymentBackend>(sp => sp.GetRequiredService<RecordingMountingDeploymentBackend>()));
        return services;
    }
}
