using System.Collections.Concurrent;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.Abstractions.Games.Merge;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FileHashes;

/// <summary>
/// Trusts the game folder's copy of a file as a merge base only on positive evidence: the installation's local
/// baseline lists the path, does not report it non-vanilla, and recorded the same hash as the bytes just read.
/// Absence of a non-vanilla row is not enough, because a baseline also leaves unlisted, ignored and unreadable files
/// without one, and a file edited after the baseline was built has no row either.
/// </summary>
public sealed class BaselineVanillaTrust(IFileHashesService fileHashes) : IVanillaTrust
{
    private sealed record BaselineIndex(TxId Basis, Dictionary<string, Hash> Recorded, Dictionary<string, NonVanillaReason> NonVanilla);

    // The merge engine asks about every contested path on each refresh, so the rows are read once per baseline
    // and database state instead of once per question.
    private readonly ConcurrentDictionary<EntityId, BaselineIndex> _indexes = new();

    public bool IsTrusted(GameInstallation installation, GamePath path, MultiHash currentHashes, out string reason)
    {
        // The baseline only describes the game folder; a file kept elsewhere has no vanilla list to check against.
        if (path.LocationId != LocationId.Game)
        {
            reason = "outside the game folder";
            return false;
        }

        var db = fileHashes.Local;
        var baseline = LocalBaselineCoordinator.FindBaseline(db, installation.LocatorResult.Store, LocalBaselineCoordinator.GamePathOf(installation));
        if (baseline is null)
        {
            reason = "the installed files have not been checked against the store's file list";
            return false;
        }

        var index = IndexOf(db, baseline.Value);
        // Manifests built on Windows and the folder on disk can differ in case.
        var wanted = path.Path.ToString();

        if (index.NonVanilla.TryGetValue(wanted, out var listed))
        {
            reason = $"listed as {Describe(listed)}";
            return false;
        }

        if (!index.Recorded.TryGetValue(wanted, out var recorded))
        {
            reason = "the baseline never checked this file";
            return false;
        }

        if (recorded != currentHashes.XxHash3)
        {
            reason = "the game's copy differs from the checked baseline";
            return false;
        }

        reason = "";
        return true;
    }

    private BaselineIndex IndexOf(IDb db, LocalBaseline.ReadOnly baseline)
    {
        if (_indexes.TryGetValue(baseline.Id, out var cached) && cached.Basis == db.BasisTxId) return cached;

        var recorded = new Dictionary<string, Hash>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in baseline.Files)
            recorded[file.Path.ToString()] = file.Hash.XxHash3;

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
