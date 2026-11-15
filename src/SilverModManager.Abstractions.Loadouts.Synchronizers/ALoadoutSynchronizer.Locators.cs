using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using CommunityToolkit.HighPerformance.Buffers;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.Abstractions.GC;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Exceptions;
using SilverModManager.Abstractions.Loadouts.Files.Diff;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Sdk.GeneratedCaches;
using SilverModManager.Abstractions.Loadouts.Sorting;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.IndexSegments;
using SilverModManager.MnemonicDB.Abstractions.Query;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.ForeignDeployments;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;
using Reloaded.Memory.Extensions;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

using DiskState = Entities<DiskStateEntry.ReadOnly>;

public partial class ALoadoutSynchronizer
{
    /// <summary>
    /// Updates the locator IDs on the loadout if the game has been updated by the store.
    /// This should be called before building the sync tree.
    /// </summary>
    private async ValueTask<(Loadout.ReadOnly Loadout, GameInstallation Installation)> UpdateLocatorIds(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
    {
        var registered = loadout.InstallationInstance;
        if (!TryLocateNow(registered, out var located))
        {
            // NOTE(erri120): It would be very odd if we re-query the game, and it's not installed anymore
            Logger.LogCritical("Found no installation of the game `{Store}`/`{Game}` anymore!", loadout.Installation.Store, loadout.Game.DisplayName);
            return (loadout, registered);
        }

        var gameLocatorResult = located.LocatorResult;

        // Judged on what the store says now, not on the registry's copy. While the store writes the files they match
        // no single build, so nothing read from them (the index, the backups, a merge base) can be trusted.
        if (gameLocatorResult.StoreUpdateInProgress)
            throw new SynchronizerPreflightException($"{gameLocatorResult.Store} is updating {registered.Game.DisplayName}; try again when it finishes (resume the update in {gameLocatorResult.Store} if it is paused). Nothing was changed.");
        if (gameLocatorResult.StoreFilesDamaged)
            throw new SynchronizerPreflightException($"{gameLocatorResult.Store} reports some of {registered.Game.DisplayName}'s files as missing or damaged. Verify the game's files in {gameLocatorResult.Store}, then apply again. Nothing was changed.");

        // The registry keeps the ids the game was found with until the app restarts, and every later read of
        // loadout.InstallationInstance (the apply, reconcile, override reprocessing, the store's attested file
        // list) would look the build up by them. Hand the registry the install as it is now.
        if (!gameLocatorResult.HasSameStoreStateAs(registered.LocatorResult))
            _gameRegistry.RefreshInstallation(located);

        var metadataLocatorIds = gameLocatorResult.LocatorIds;
        var newLocatorIds = metadataLocatorIds.Distinct().ToArray();

        if (newLocatorIds.Length != metadataLocatorIds.Length)
            Logger.LogWarning("Found duplicate locator IDs `{LocatorIds}` on gameLocatorResult for game `{Game}` while updating locator IDs", metadataLocatorIds, loadout.InstallationInstance.Game.DisplayName);

        var locatorsToAdd = newLocatorIds.Except(loadout.LocatorIds).ToArray();
        var locatorsToRemove = loadout.LocatorIds.Except(newLocatorIds).ToArray();
        var changed = locatorsToAdd.Length != 0 || locatorsToRemove.Length != 0;

        // Everything below that depends on the build works from the install as it is located now.
        var baseline = FindLocalBaseline(located);
        var baselineIsForAnotherBuild = baseline is { } found && !newLocatorIds.Select(id => id.Value).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(found.LocatorIds);

        // No reason to change the loadout if the version is the same. The install handed back is the one located
        // now: a first apply may have adopted the new ids already, leaving the loadout matching them. The registry
        // is refreshed above when the build changes, and returning the located install means this synchronize does
        // not depend on that refresh having reached the copy the index reads.
        if (!changed && !baselineIsForAnotherBuild) return (loadout, located);

        // A baseline built for another build vouches for nothing the loadout's ids name, so without a rebuild every
        // unchanged vanilla file reads as one the loadout deleted. The background pass can have failed or not
        // run since the update, so the apply makes sure itself.
        var managed = GameInstallMetadata.LastSyncedLoadout.TryGetValue(loadout.Installation, out _);
        var covered = (gameLocatorResult.Store, newLocatorIds);
        if (baseline is not null || managed)
        {
            // A managed folder whose build the store data already covers needs no baseline of its own.
            if (!managed || !_fileHashService.CoversInstalledBuild(covered))
                await RebuildBaselineForUpdatedBuild(located, cancellationToken);

            // The build has to be described by something: the store's file list, or a baseline taken from the folder
            // that leaves out what the loadout deployed. When the rebuild could not produce one (the loadout that
            // was applied is gone, so what it deployed is unknown), the deployed mods would pass for the game's own.
            if (managed && !_fileHashService.CoversInstalledBuild(covered) && !HasBaselineFor(located, newLocatorIds))
            {
                var howToLift = gameLocatorResult.Store == GameStore.Steam
                    ? "Let Steam finish updating the game (it keeps the file list for a build in its depotcache), then apply again."
                    : "Apply again once the app's game file data covers the new build.";
                throw new SynchronizerPreflightException(
                    $"{registered.Game.DisplayName} was updated to a build the app has no game file list for yet, so it cannot tell the game's own files from added ones and will not change the folder. " +
                    $"Nothing was changed. {howToLift}");
            }
        }

        if (!changed) return (loadout, located);

        if (Logger.IsEnabled(LogLevel.Information))
        {
            var sCurrent = loadout.LocatorIds.Select(x => x.Value).ToArray();
            var sToAdd = locatorsToAdd.Select(x => x.Value).ToArray();
            var sToRemove = locatorsToRemove.Select(x => x.Value).ToArray();
            Logger.LogInformation("Locator IDs changed Current=`{CurrentIds}` ToAdd=`{ToAdd}` ToRemove=`{ToRemove}`", sCurrent, sToAdd, sToRemove);
        }

        using var tx = Connection.BeginTransaction();

        if (_fileHashService.TryGetVanityVersion((gameLocatorResult.Store, newLocatorIds), out var vanityVersion))
        {
            tx.Add(loadout, Loadout.GameVersion, vanityVersion);
        }
        else
        {
            tx.Add(loadout, Loadout.GameVersion, VanityVersion.DefaultValue);
            Logger.LogWarning("Found no vanity version for locator IDs `{LocatorIds}` (`{Store}`)", newLocatorIds, gameLocatorResult.Store);
        }

        foreach (var id in locatorsToRemove)
            tx.Retract(loadout, Loadout.LocatorIds, id);
        foreach (var id in locatorsToAdd)
            tx.Add(loadout, Loadout.LocatorIds, id);

        var result = await tx.Commit();
        return (loadout.Rebase(result.Db), located);
    }

    /// <summary>
    /// Asks the store again where the game is and which build it is. The registry's copy keeps the ids the game was
    /// found with, and the store's file list is looked up by those ids. When the build has changed,
    /// <see cref="UpdateLocatorIds"/> hands the registry this install through <c>RefreshInstallation</c>; it also
    /// returns it, so the running synchronize reads the folder against the current build whether or not that refresh
    /// has been seen by every reader.
    /// </summary>
    private static bool TryLocateNow(GameInstallation registered, [NotNullWhen(true)] out GameInstallation? located)
    {
        located = null;
        if (!registered.LocatorResult.Locator.TryLocate(registered.Game, out var result)) return false;
        located = new GameInstallation(result, registered.Locations);
        return true;
    }

    private LocalBaseline.ReadOnly? FindLocalBaseline(GameInstallation installation)
    {
        var path = installation.Locations[LocationId.Game].Path.ToString();
        foreach (var baseline in LocalBaseline.FindByGamePath(_fileHashService.Local, path))
        {
            if (baseline.Store == installation.LocatorResult.Store) return baseline;
        }

        return null;
    }

    private bool HasBaselineFor(GameInstallation installation, LocatorId[] locatorIds)
        => FindLocalBaseline(installation) is { } baseline
           && locatorIds.Select(id => id.Value).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(baseline.LocatorIds);

    private async ValueTask RebuildBaselineForUpdatedBuild(GameInstallation located, CancellationToken cancellationToken)
    {
        try
        {
            await _fileHashService.EnsureLocalBaseline(located, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The coverage check that follows decides whether the apply can go on without it.
            Logger.LogWarning(e, "Could not rebuild the vanilla file list of {Game} for its updated build", located.Game.DisplayName);
        }
    }

    /// <param name="recordAsApplied">The edit follows an apply, so the record of the last applied loadout is moved
    /// onto it. The rows dropped were never deployed, so the applied state is the same without them.</param>
    private async ValueTask<Loadout.ReadOnly> ReprocessOverrides(Loadout.ReadOnly loadout, bool recordAsApplied)
    {
        // Make a lookup set of the new files based on current locator IDs
        var versionFiles = _fileHashService
            .GetGameFiles(loadout.InstallationInstance, loadout.LocatorIds)
            .Select(file => file.Path)
            .ToHashSet();

        // Find all files in the overrides that match a path in the new files, or that sit on a path the sync will
        // never deploy to (an earlier build adopted them). Those are retired, not promoted: the tree skips them
        // anyway, so dropping the row only stops it being listed as a loadout file and logging a warning per build.
        var toDelete = (from grp in LoadoutItem.FindByLoadout(loadout.Db, loadout).OfTypeLoadoutItemGroup().OfTypeLoadoutOverridesGroup()
            from item in grp.AsLoadoutItemGroup().Children.OfTypeLoadoutItemWithTargetPath()
            let path = (GamePath)item.TargetPath
            where versionFiles.Contains(path) || IsGameOutputOnFilteredPath(path) || IsSkippedAsProtected(item.AsLoadoutItem(), path)
            select (Item: item, Promote: versionFiles.Contains(path))).ToList();

        // No files to process, return early
        if (toDelete.Count == 0)
            return loadout;

        using var tx = Connection.BeginTransaction();
        var gameMetadataId = loadout.InstallationId;

        // Delete all the matching override files
        foreach (var (file, promote) in toDelete)
        {
            tx.Delete(file, recursive: false);

            // A file the game writes is not a game file to root, only a row that should never have been filed.
            if (!promote)
                continue;

            // The backed up file is being 'promoted' to a game file, which needs
            // to be rooted explicitly in case the user uses a feature like 'undo'
            // to roll back a game version on a store (like Xbox/Epic) which does
            // not support downloading non-current version(s).
            if (!file.TryGetAsLoadoutFile(out var loadoutFile))
                continue;

            _ = new GameBackedUpFile.New(tx)
            {
                Hash = loadoutFile.Hash,
                GameInstallId = gameMetadataId,
            };
        }

        if (recordAsApplied)
            tx.Add(gameMetadataId, Sdk.Games.GameInstallMetadata.LastSyncedLoadoutTransaction, EntityId.From(tx.ThisTxId.Value));

        var result = await tx.Commit();
        return loadout.Rebase(result.Db);
    }

    /// <summary>
    /// Whether an override on this path can only be something the game wrote itself and the sync leaves alone. An
    /// earlier build that did not filter the path filed the game's output as the player's overrides; the sync never
    /// deploys such a row, so it is dropped rather than left to read as a mod that cannot be deployed.
    /// </summary>
    private bool IsGameOutputOnFilteredPath(GamePath path) => IsGameOrCacheOutput(path) && GamePathFilter.ShouldFilter(path);

    /// <summary>
    /// Whether the path is something the game or a mod's generated cache writes, so never the loadout's to deploy.
    /// A cache folder an earlier build indexed has disk state and Overrides rows that are dropped the way the
    /// game's own output is; the files stay on disk.
    /// </summary>
    private bool IsGameOrCacheOutput(GamePath path) => IsGameWrittenPath(path) || _cacheClearer.IsOwnedPath(path);
}
