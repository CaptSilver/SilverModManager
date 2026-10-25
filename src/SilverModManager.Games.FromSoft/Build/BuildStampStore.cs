using SilverModManager.Games.FromSoft.Heal;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FromSoft.Build;

/// <summary>Reads and writes the <see cref="FromSoftBuildStamp"/> of an installation.</summary>
public sealed class BuildStampStore(IConnection connection, TimeProvider time, RegulationBaselines baselines)
{
    /// <summary>The stamped build of the installation, or null when it was never stamped.</summary>
    public BuildIdentity? Find(GameInstallMetadataId installation)
    {
        var stamp = FromSoftBuildStamp.FindByInstallation(connection.Db, installation).FirstOrDefault();
        return stamp.IsValid() ? Read(stamp) : null;
    }

    /// <summary>Stamps the installation with the build, replacing the stamp it had.</summary>
    public async Task Record(GameInstallMetadataId installation, BuildIdentity identity)
    {
        using var tx = connection.BeginTransaction();
        foreach (var old in FromSoftBuildStamp.FindByInstallation(connection.Db, installation))
            tx.Delete(old.Id, recursive: false);

        var stamp = new FromSoftBuildStamp.New(tx)
        {
            InstallationId = installation,
            Exe = identity.Exe,
            SteamBuildId = identity.SteamBuildId,
            RecordedAt = time.GetUtcNow().UtcDateTime,
        };
        AddRegulation(tx, stamp.Id, identity.RegulationVersion, identity.RegulationHash);
        await tx.Commit();
    }

    /// <summary>
    /// Writes the regulation fields <paramref name="live"/> has into the existing stamp that lacks them, leaving the
    /// rest of the stamp, including when it was recorded, as it was. Returns whether anything was written.
    /// </summary>
    public async Task<bool> Backfill(GameInstallMetadataId installation, BuildIdentity live)
    {
        var stamp = FromSoftBuildStamp.FindByInstallation(connection.Db, installation).FirstOrDefault();
        if (!stamp.IsValid()) return false;

        var current = Read(stamp);
        var version = current.RegulationVersion is null ? live.RegulationVersion : null;
        var hash = current.RegulationHash is null ? live.RegulationHash : null;
        if (version is null && hash is null) return false;

        using var tx = connection.BeginTransaction();
        AddRegulation(tx, stamp.Id, version, hash);
        await tx.Commit();
        return true;
    }

    private static BuildIdentity Read(FromSoftBuildStamp.ReadOnly stamp)
        => new(stamp.Exe, stamp.SteamBuildId,
            stamp.RegulationVersion.HasValue ? stamp.RegulationVersion.Value : null,
            stamp.RegulationHash.HasValue ? stamp.RegulationHash.Value : null);

    // The optional attributes are written only when there is a value, so a stamp never holds an empty string for a field it lacks.
    private static void AddRegulation(ITransaction tx, EntityId stamp, string? version, string? hash)
    {
        if (version is not null) tx.Add(stamp, FromSoftBuildStamp.RegulationVersion, version);
        if (hash is not null) tx.Add(stamp, FromSoftBuildStamp.RegulationHash, hash);
    }

    /// <summary>Replaces the stamp of the loadout's installation with the build that is installed now, and keeps a copy of its regulation.</summary>
    public async Task Restamp(LoadoutId loadoutId)
    {
        var loadout = Loadout.Load(connection.Db, loadoutId);
        if (!loadout.IsValid()) throw new InvalidOperationException($"The loadout {loadoutId} no longer exists.");

        // Re-stamp is offered for a tampered install too, so the old stamp decides whether the file is kept.
        var previous = Find(loadout.InstallationId);
        var live = BuildIdentity.Identify(loadout.InstallationInstance);
        await Record(loadout.InstallationId, live);
        baselines.KeepBuild(loadout.InstallationInstance, previous, live);
    }
}
