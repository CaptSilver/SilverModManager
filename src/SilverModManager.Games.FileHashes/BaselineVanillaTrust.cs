using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.Abstractions.Games.Merge;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FileHashes;

/// <summary>
/// Trusts the game folder's copy of a file as a merge base when the installation's local baseline checked the folder
/// against the store's file list and did not list the file as non-vanilla. Without a baseline nothing is known about
/// the copy, so it is not trusted either.
/// </summary>
public sealed class BaselineVanillaTrust(IFileHashesService fileHashes) : IVanillaTrust
{
    public bool IsTrusted(GameInstallation installation, GamePath path, out string reason)
    {
        // The baseline only describes the game folder; a file kept elsewhere has no vanilla list to check against.
        if (path.LocationId != LocationId.Game)
        {
            reason = "outside the game folder";
            return false;
        }

        var db = fileHashes.Local;
        var store = installation.LocatorResult.Store;
        var baseline = LocalBaseline.FindByGamePath(db, installation.LocatorResult.Path.ToString())
            .Where(b => b.Store == store)
            .Cast<LocalBaseline.ReadOnly?>()
            .FirstOrDefault();
        if (baseline is null)
        {
            reason = "the installed files have not been checked against the store's file list";
            return false;
        }

        var wanted = path.Path.ToString();
        foreach (var file in NonVanillaFile.FindByBaseline(db, baseline.Value.Id))
        {
            // Windows-built manifests and the folder on disk can differ in case.
            if (!string.Equals(file.Path.ToString(), wanted, StringComparison.OrdinalIgnoreCase)) continue;

            reason = $"listed as {Describe(file.Reason)}";
            return false;
        }

        reason = "";
        return true;
    }

    private static string Describe(NonVanillaReason reason) => reason switch
    {
        NonVanillaReason.NotInManifest => "not a file the store installed",
        NonVanillaReason.SizeDiffers => "a different size from the store's copy",
        _ => "modified",
    };
}
