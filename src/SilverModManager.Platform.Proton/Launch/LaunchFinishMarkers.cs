using Microsoft.Extensions.Logging;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Platform.Proton.Launch;

/// <summary>
/// How a pending-finish marker stands against what has been committed since it was written.
/// </summary>
public enum PendingLaunchFinishState
{
    /// <summary>The marked loadout is still the last one synced and no sync has been recorded since the marker was written.</summary>
    Pending,

    /// <summary>A sync was committed after the marker was written, or another loadout was synced last. The ingest the marker asks for already happened or no longer applies.</summary>
    AlreadySynced,

    /// <summary>The marked loadout no longer exists.</summary>
    LoadoutGone,
}

/// <summary>
/// A launch that reached Running and has not yet had its after-game ingest committed. <see cref="LoadoutName"/> is
/// null when the loadout is gone.
/// </summary>
public sealed record PendingLaunchFinish(LoadoutId LoadoutId, string? LoadoutName, Guid SessionId,
    IReadOnlyList<string> GateNames, PendingLaunchFinishState State);

/// <summary>
/// The durable record that a launch's after-game ingest is still owed, kept on the game's
/// <see cref="GameInstallMetadata"/> so it outlives the process that was watching the game.
/// </summary>
public interface ILaunchFinishMarkers
{
    /// <summary>Records that the launch of <paramref name="loadout"/> is running and its finish is owed. One commit.</summary>
    Task Write(GameInstallation installation, LoadoutId loadout, Guid sessionId, IReadOnlyList<string> gateNames);

    /// <summary>The marker for the installation, or null when none is set.</summary>
    PendingLaunchFinish? Read(GameInstallation installation);

    /// <summary>Clears the marker. True when one was there when checked; when none was, nothing is committed.</summary>
    Task<bool> Retract(GameInstallation installation);
}

/// <remarks>
/// Gate names are <c>GetType().Name</c>, which is what the launch log lines print. Two gates in different assemblies
/// that share a type name cannot be told apart by it; that gap is accepted.
/// </remarks>
internal sealed class LaunchFinishMarkers(IConnection connection, IGameRegistry registry, ILogger<LaunchFinishMarkers> logger) : ILaunchFinishMarkers
{
    public async Task Write(GameInstallation installation, LoadoutId loadout, Guid sessionId, IReadOnlyList<string> gateNames)
    {
        var metadata = registry.ForceGetMetadata(installation);
        using var tx = connection.BeginTransaction();
        tx.Add(metadata.Id, GameInstallMetadata.LaunchFinishPending, loadout.Value);
        tx.Add(metadata.Id, GameInstallMetadata.LaunchFinishPendingSession, sessionId.ToString("N"));
        foreach (var name in gateNames) tx.Add(metadata.Id, GameInstallMetadata.LaunchFinishPendingGates, name);
        await tx.Commit();
        logger.LogDebug("Marked the finish of {Game} as pending for loadout {Loadout}, session {Session}, gates [{Gates}]",
            installation.Game.DisplayName, loadout, sessionId, string.Join(", ", gateNames));
    }

    public PendingLaunchFinish? Read(GameInstallation installation)
    {
        if (!registry.TryGetMetadata(installation, out var metadata)) return null;
        // Everything below reads from the snapshot the metadata came from, so a commit landing mid-read cannot
        // show the marker in one snapshot and its transaction or the last-sync fields in another.
        var db = metadata.Db;
        if (!GameInstallMetadata.LaunchFinishPending.TryGetValue(metadata, out var markedId)) return null;

        var loadoutId = LoadoutId.From(markedId);
        var gates = GameInstallMetadata.LaunchFinishPendingGates.Get(metadata).ToList();
        var sessionId = GameInstallMetadata.LaunchFinishPendingSession.TryGetValue(metadata, out var session) && Guid.TryParse(session, out var parsed)
            ? parsed
            : Guid.Empty;

        var loadout = Loadout.Load(db, loadoutId);
        if (!loadout.IsValid() || !loadout.IsVisible())
        {
            logger.LogTrace("The marker on {Game} names loadout {Loadout}, which is gone", installation.Game.DisplayName, loadoutId);
            return new PendingLaunchFinish(loadoutId, null, sessionId, gates, PendingLaunchFinishState.LoadoutGone);
        }

        // The marker's value is in this snapshot, so its datom is too; a missing one means no marker.
        if (MarkerTransaction(db, metadata.Id) is not { } markerTx) return null;
        var state = PendingLaunchFinishState.Pending;
        if (GameInstallMetadata.LastSyncedLoadout.TryGetValue(metadata, out var lastSynced) && lastSynced != markedId)
        {
            state = PendingLaunchFinishState.AlreadySynced;
        }
        else if (GameInstallMetadata.LastSyncedLoadoutTransaction.TryGetValue(metadata, out var syncTx)
                 && syncTx.Value >= markerTx.Value)
        {
            state = PendingLaunchFinishState.AlreadySynced;
        }

        logger.LogTrace("The marker on {Game} for loadout {Loadout} reads as {State}", installation.Game.DisplayName, loadoutId, state);
        return new PendingLaunchFinish(loadoutId, loadout.Name, sessionId, gates, state);
    }

    public async Task<bool> Retract(GameInstallation installation)
    {
        if (!registry.TryGetMetadata(installation, out var metadata)) return false;
        if (MarkerTransaction(metadata.Db, metadata.Id) is null) return false;

        // A sync that retracts the marker between the check above and the commit makes the retractions below
        // no-ops; the caller still hears true. Harmless, so the race is accepted.

        using var tx = connection.BeginTransaction();
        tx.RetractCurrent(metadata.Id, GameInstallMetadata.LaunchFinishPending);
        tx.RetractCurrent(metadata.Id, GameInstallMetadata.LaunchFinishPendingSession);
        tx.RetractCurrent(metadata.Id, GameInstallMetadata.LaunchFinishPendingGates);
        await tx.Commit();
        logger.LogDebug("Cleared the pending finish marker on {Game}", installation.Game.DisplayName);
        return true;
    }

    /// <summary>The transaction that wrote the marker's loadout reference, or null when it is not set.</summary>
    private static TxId? MarkerTransaction(IDb db, EntityId metadataId)
    {
        var attribute = db.AttributeCache.GetAttributeId(GameInstallMetadata.LaunchFinishPending.Id);
        foreach (var datom in db.Datoms(metadataId))
        {
            if (datom.A == attribute) return datom.T;
        }
        return null;
    }
}
