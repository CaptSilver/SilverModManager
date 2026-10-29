using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Abstractions.Games.Merge;

/// <summary>
/// Decides whether the installed copy of a game file can stand in for the vanilla ancestor of a merge. A file the
/// player or another tool has changed would make every difference between it and a mod read as a mod edit.
/// </summary>
public interface IVanillaTrust
{
    /// <summary>
    /// Whether the installed copy at <paramref name="path"/> is the file a baseline recorded. Trust needs positive
    /// evidence: the baseline lists the path and <paramref name="currentHashes"/>, the hashes of the bytes the caller just
    /// read, hold the one the baseline recorded, whichever algorithm it used. A missing baseline, an unlisted path, a file listed non-vanilla or a file changed
    /// since the baseline was built is untrusted; <paramref name="reason"/> is a phrase for the note. A missing
    /// baseline is not by itself a refusal: the store's own file list for the installed build is asked next.
    /// </summary>
    bool IsTrusted(GameInstallation installation, GamePath path, MultiHash currentHashes, out string reason);
}

/// <summary>Reasons <see cref="IVanillaTrust"/> gives that other code tells apart.</summary>
public static class VanillaTrustReasons
{
    /// <summary>
    /// No baseline matches the installed build and the store has no file list for it that the app can read yet
    /// (Steam has not cached the depot manifests). The only reason that means the copy was not checked, as opposed
    /// to checked and found different.
    /// </summary>
    public const string StoreFileListNotSavedYet = "Steam has not saved the file list for the installed build yet";

    /// <summary>The same for a store with no on-disk file list the app can read at all.</summary>
    public const string NotCheckedAgainstStoreList = "the installed files have not been checked against the store's file list";

    /// <summary>The copy is not the one the store's file list for the installed build gives.</summary>
    public const string DiffersFromStoreList = "the game's copy differs from the store's file list for the installed build";

    /// <summary>The store's file list for the installed build does not have the file.</summary>
    public const string NotInstalledByStore = "not a file the store installed";

    /// <summary>
    /// Steam saved the file list for the installed build but the app cannot use it: it needs a depot key the app does
    /// not have, or the file could not be read or parsed.
    /// </summary>
    public const string StoreFileListUnreadable = "Steam's saved file list for the installed build cannot be read by the app";

    /// <summary>The baseline lists the file as changed from the store's copy.</summary>
    public const string ListedAsModified = "listed as modified";

    /// <summary>The baseline lists the file with another size than the store's copy.</summary>
    public const string ListedAsSizeDiffers = "listed as a different size from the store's copy";

    /// <summary>The file was changed after the baseline was built.</summary>
    public const string DiffersFromBaseline = "the game's copy differs from the checked baseline";
}
