using SilverModManager.Sdk.Library;

namespace SilverModManager.Abstractions.Library;

/// <summary>
/// A batch of one source's updates to apply.
/// </summary>
/// <param name="Updates">Updates made by the source applying them.</param>
/// <param name="Replace">Swap the new files in for the current ones in every loadout, rather than only adding them to the library.</param>
/// <param name="MayOpenBrowser">
/// False for Update All. A source that can only update through the website (a free Nexus Mods account) then opens
/// nothing and sets <see cref="ModUpdateOutcome.AccountCannotDownload"/> instead.
/// </param>
public sealed record ModUpdateRequest(IReadOnlyList<ModUpdate> Updates, bool Replace, bool MayOpenBrowser);

/// <summary>
/// What applying a <see cref="ModUpdateRequest"/> did. The source shows no dialogs; the caller decides what to tell
/// the user from this.
/// </summary>
/// <param name="Downloaded">The new library items.</param>
/// <param name="Replacements">One per old item swapped for a new one, with how the swap went.</param>
/// <param name="DownloadFailures">The downloads that failed, each narrowed to the part of the update that failed.</param>
/// <param name="OpenedInBrowser">The links the app handed to the browser (a free Nexus Mods account downloads there).</param>
/// <param name="AccountCannotDownload">The source could have applied updates only by opening the browser, and was not allowed to.</param>
public sealed record ModUpdateOutcome(
    IReadOnlyList<LibraryItem.ReadOnly> Downloaded,
    IReadOnlyList<ModUpdateReplacement> Replacements,
    IReadOnlyList<ModUpdateFailure> DownloadFailures,
    IReadOnlyList<Uri> OpenedInBrowser,
    bool AccountCannotDownload)
{
    /// <summary>
    /// An outcome in which nothing happened.
    /// </summary>
    public static ModUpdateOutcome Nothing { get; } = new([], [], [], [], false);
}

/// <summary>
/// An old library item swapped for a new one in the loadouts that linked it.
/// </summary>
public readonly record struct ModUpdateReplacement(LibraryItem.ReadOnly Old, LibraryItem.ReadOnly New, LibraryItemReplacementResult Result);

/// <summary>
/// A download that failed. <see cref="Update"/> is narrowed to what failed, so applying it again retries exactly that
/// and nothing that already succeeded.
/// </summary>
public readonly record struct ModUpdateFailure(ModUpdate Update, string Name, Exception Error);
