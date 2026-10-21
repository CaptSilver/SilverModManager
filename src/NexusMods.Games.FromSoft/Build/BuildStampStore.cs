using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.FromSoft.Build;

/// <summary>Reads and writes the <see cref="EldenRingBuildStamp"/> of an installation.</summary>
public sealed class BuildStampStore(IConnection connection, TimeProvider time)
{
    /// <summary>The stamped build of the installation, or null when it was never stamped.</summary>
    public BuildIdentity? Find(GameInstallMetadataId installation)
    {
        var stamp = EldenRingBuildStamp.FindByInstallation(connection.Db, installation).FirstOrDefault();
        return stamp.IsValid() ? new BuildIdentity(stamp.Exe, stamp.SteamBuildId) : null;
    }

    /// <summary>Stamps the installation with the build, replacing the stamp it had.</summary>
    public async Task Record(GameInstallMetadataId installation, BuildIdentity identity)
    {
        using var tx = connection.BeginTransaction();
        foreach (var old in EldenRingBuildStamp.FindByInstallation(connection.Db, installation))
            tx.Delete(old.Id, recursive: false);

        _ = new EldenRingBuildStamp.New(tx)
        {
            InstallationId = installation,
            Exe = identity.Exe,
            SteamBuildId = identity.SteamBuildId,
            RecordedAt = time.GetUtcNow().UtcDateTime,
        };
        await tx.Commit();
    }

    /// <summary>Replaces the stamp of the loadout's installation with the build that is installed now.</summary>
    public Task Restamp(LoadoutId loadoutId)
    {
        var loadout = Loadout.Load(connection.Db, loadoutId);
        if (!loadout.IsValid()) throw new InvalidOperationException($"The loadout {loadoutId} no longer exists.");

        return Record(loadout.InstallationId, BuildIdentity.Identify(loadout.InstallationInstance));
    }
}
