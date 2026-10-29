using System.Collections.Concurrent;
using NexusMods.Hashing.xxHash3;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.Abstractions.Games.Merge;
using SilverModManager.Abstractions.Steam;
using SilverModManager.Abstractions.Steam.Values;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FileHashes;

/// <summary>
/// Trusts the game folder's copy of a file as a merge base only on positive evidence: the installation's local
/// baseline lists the path, does not report it non-vanilla, and recorded the same hash as the bytes just read. The
/// recorded hash is an xxHash3 for a file the baseline hashed and a SHA-1 for one it took from the manifest, so the
/// bytes are compared by the kind the baseline recorded.
/// Absence of a non-vanilla row is not enough, because a baseline also leaves unlisted, ignored and unreadable files
/// without one, and a file edited after the baseline was built has no row either.
/// </summary>
public sealed class BaselineVanillaTrust(IFileHashesService fileHashes, IDepotManifestSource? manifestSource = null, ILogger<BaselineVanillaTrust>? logger = null) : IVanillaTrust
{
    private sealed record BaselineIndex(TxId Basis, Dictionary<string, FileIdentity> Recorded, Dictionary<string, NonVanillaReason> NonVanilla);

    // The merge engine asks about every contested path on each refresh, so the rows are read once per baseline
    // and database state instead of once per question.
    private readonly ConcurrentDictionary<EntityId, BaselineIndex> _indexes = new();

    // What the store's file list for an installed build says, for the window before a baseline exists for it (a
    // game updated while the app ran). Keyed by the installation and the database state, like the baseline index.
    private sealed record StoreListIndex(TxId Basis, Dictionary<string, FileIdentity> Recorded, bool Complete);
    private readonly ConcurrentDictionary<string, StoreListIndex> _storeLists = new();

    // A manifest id names immutable content, so a parsed manifest is kept for good. A read that failed is not kept:
    // Steam may save the file later.
    private readonly ConcurrentDictionary<ulong, Dictionary<string, Sha1Value>> _manifests = new();
    private readonly ConcurrentDictionary<ulong, byte> _loggedUnreadable = new();

    public bool IsTrusted(GameInstallation installation, GamePath path, MultiHash currentHashes, out string reason)
    {
        // The baseline only describes the game folder; a file kept elsewhere has no vanilla list to check against.
        if (path.LocationId != LocationId.Game)
        {
            reason = "outside the game folder";
            return false;
        }

        var db = fileHashes.Local;
        var baseline = LocalBaselineCoordinator.FindBaselineFor(db, installation);
        if (baseline is null) return IsTrustedByStoreList(db, installation, path, currentHashes, out reason);

        var index = IndexOf(db, baseline.Value);
        // Manifests built on Windows and the folder on disk can differ in case.
        var wanted = path.Path.ToString();

        if (index.NonVanilla.TryGetValue(wanted, out var listed))
        {
            reason = listed switch
            {
                NonVanillaReason.NotInManifest => $"listed as {Describe(listed)}",
                NonVanillaReason.SizeDiffers => VanillaTrustReasons.ListedAsSizeDiffers,
                _ => VanillaTrustReasons.ListedAsModified,
            };
            return false;
        }

        if (!index.Recorded.TryGetValue(wanted, out var recorded))
        {
            reason = "the baseline never checked this file";
            return false;
        }

        return Matches(recorded, currentHashes, VanillaTrustReasons.DiffersFromBaseline, out reason);
    }

    /// <summary>
    /// No baseline was built for the installed build's ids, which is the state between a store update and the
    /// next baseline build. The store's own file list for those ids still settles the question, and the hashes of
    /// the bytes are already in hand.
    /// </summary>
    private bool IsTrustedByStoreList(IDb db, GameInstallation installation, GamePath path, MultiHash currentHashes, out string reason)
    {
        var wanted = path.Path.ToString();
        var index = StoreListOf(db, installation);
        var complete = index.Complete;
        var unreadable = false;

        if (index.Recorded.TryGetValue(wanted, out var recorded))
            return Matches(recorded, currentHashes, VanillaTrustReasons.DiffersFromStoreList, out reason);

        if (installation.LocatorResult.Store == GameStore.Steam)
        {
            switch (FromSavedManifests(installation, wanted, currentHashes, out reason))
            {
                case Verdict.Found: return reason.Length == 0;
                case Verdict.NotListedInAnyManifest: complete = true; break;
                case Verdict.SomeUnreadable: unreadable = true; break;
            }
        }

        if (complete)
        {
            reason = VanillaTrustReasons.NotInstalledByStore;
            return false;
        }

        if (unreadable)
        {
            reason = VanillaTrustReasons.StoreFileListUnreadable;
            return false;
        }

        reason = installation.LocatorResult.Store == GameStore.Steam ? VanillaTrustReasons.StoreFileListNotSavedYet : VanillaTrustReasons.NotCheckedAgainstStoreList;
        return false;
    }

    // The recorded digest and the current one are compared in the recorded kind. Both sides come from the same
    // MultiHash family, so the kinds agree by construction and Equals is the whole question.
    private static bool Matches(FileIdentity recorded, MultiHash currentHashes, string differsReason, out string reason)
    {
        FileIdentity current;
        try
        {
            current = FileIdentity.Of(currentHashes, recorded.Kind);
        }
        catch (InvalidOperationException)
        {
            reason = "recorded with a hash kind this build cannot compute";
            return false;
        }

        if (!recorded.Equals(current))
        {
            reason = differsReason;
            return false;
        }

        reason = "";
        return true;
    }

    private StoreListIndex StoreListOf(IDb db, GameInstallation installation)
    {
        var locator = installation.LocatorResult;
        var key = $"{locator.Store}|{LocalBaselineCoordinator.GamePathOf(installation)}|{string.Join(',', locator.LocatorIds.Select(id => id.Value).Order(StringComparer.OrdinalIgnoreCase))}";
        if (_storeLists.TryGetValue(key, out var cached) && cached.Basis == db.BasisTxId) return cached;

        var recorded = new Dictionary<string, FileIdentity>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in fileHashes.GetGameFiles(installation))
            recorded[file.Path.Path.ToString()] = file.Identity;

        var index = new StoreListIndex(db.BasisTxId, recorded, fileHashes.CoversInstalledBuild(new LocatorIdsWithGameStore(locator.Store, locator.LocatorIds.ToArray())));
        _storeLists[key] = index;
        return index;
    }

    private enum Verdict { Found, NotListedInAnyManifest, SomeUnreadable, Unknown }

    /// <summary>
    /// Reads the depot manifests Steam saved for the installed build's ids. <see cref="Verdict.Found"/> carries the
    /// answer in <paramref name="reason"/> (empty means the SHA-1 matched); the other two say whether every id's
    /// manifest was readable and none lists the file, or some manifest could not be read.
    /// </summary>
    private Verdict FromSavedManifests(GameInstallation installation, string wanted, MultiHash currentHashes, out string reason)
    {
        reason = "";
        var locator = installation.LocatorResult;
        if (manifestSource is null || locator.StoreRoot is not { } storeRoot) return Verdict.Unknown;

        var allRead = !locator.LocatorIds.IsEmpty;
        var anyUnreadable = false;
        foreach (var id in locator.LocatorIds)
        {
            if (!ulong.TryParse(id.Value, out var manifestId)) { allRead = false; continue; }
            var files = ManifestFiles(manifestSource, storeRoot, locator.Path, manifestId, out var unreadable);
            if (files is null) { allRead = false; anyUnreadable |= unreadable; continue; }
            if (!files.TryGetValue(wanted, out var sha1)) continue;

            if (sha1 == currentHashes.Sha1) return Verdict.Found;
            reason = VanillaTrustReasons.DiffersFromStoreList;
            return Verdict.Found;
        }

        return allRead ? Verdict.NotListedInAnyManifest : anyUnreadable ? Verdict.SomeUnreadable : Verdict.Unknown;
    }

    private Dictionary<string, Sha1Value>? ManifestFiles(IDepotManifestSource source, AbsolutePath storeRoot, AbsolutePath installPath, ulong manifestId, out bool unreadable)
    {
        unreadable = false;
        if (_manifests.TryGetValue(manifestId, out var kept)) return kept;

        try
        {
            if (!source.TryGetManifestPath(storeRoot, ManifestId.From(manifestId), installPath, out var manifestPath)) return null;
            if (source.Read(manifestPath) is not LocalManifestResult.Parsed parsed)
            {
                unreadable = true;
                if (_loggedUnreadable.TryAdd(manifestId, 0))
                    logger?.LogDebug("The saved depot manifest {ManifestId} cannot be read without Steam's depot key, so it cannot vouch for game files", manifestId);
                return null;
            }

            var files = new Dictionary<string, Sha1Value>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in parsed.Manifest.Files) files[file.Path.ToString()] = file.Hash;
            _manifests[manifestId] = files;
            return files;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Never into a merge plan: an unreadable file vouches for nothing.
            unreadable = true;
            if (_loggedUnreadable.TryAdd(manifestId, 0))
                logger?.LogDebug(e, "Could not read the saved depot manifest {ManifestId}", manifestId);
            return null;
        }
    }

    private BaselineIndex IndexOf(IDb db, LocalBaseline.ReadOnly baseline)
    {
        if (_indexes.TryGetValue(baseline.Id, out var cached) && cached.Basis == db.BasisTxId) return cached;

        var recorded = new Dictionary<string, FileIdentity>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in baseline.Files)
            recorded[file.Path.ToString()] = FileIdentity.XxHash3(file.Hash.XxHash3);
        foreach (var file in AttestedFile.FindByBaseline(db, baseline.Id))
            recorded[file.Path.ToString()] = FileIdentity.Sha1(file.Sha1);

        var nonVanilla = new Dictionary<string, NonVanillaReason>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in NonVanillaFile.FindByBaseline(db, baseline.Id))
            nonVanilla[file.Path.ToString()] = file.Reason;

        var index = new BaselineIndex(db.BasisTxId, recorded, nonVanilla);
        _indexes[baseline.Id] = index;
        return index;
    }

    private static string Describe(NonVanillaReason reason) => reason switch
    {
        NonVanillaReason.NotInManifest => "not a file the store installed",
        NonVanillaReason.SizeDiffers => "a different size from the store's copy",
        _ => "modified",
    };
}
