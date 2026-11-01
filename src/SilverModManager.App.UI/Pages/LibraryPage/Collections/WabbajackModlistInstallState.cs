using System.Reactive.Linq;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.App.UI.Pages.LibraryPage.Collections;

/// <summary>Where a modlist stands in one loadout.</summary>
public enum WabbajackModlistCardState
{
    NotInstalled,

    /// <summary>Its group exists but the install did not finish, or files were skipped.</summary>
    Incomplete,

    Installed,
}

public static class WabbajackModlistInstallState
{
    /// <summary>The modlist's group in <paramref name="loadout"/>, if it was installed there.</summary>
    public static WabbajackModlistLoadoutGroup.ReadOnly? FindGroup(IDb db, LibraryFileId list, LoadoutId loadout)
    {
        foreach (var group in WabbajackModlistLoadoutGroup.FindByModlist(db, list.Value))
        {
            if (group.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().LoadoutId == loadout) return group;
        }
        return null;
    }

    public static WabbajackModlistCardState Read(IDb db, LibraryFileId list, LoadoutId loadout) => ReadInstall(db, list, loadout).State;

    /// <summary>The state with the group it was read from, for callers that need to act on the group too.</summary>
    public static WabbajackModlistInstall ReadInstall(IDb db, LibraryFileId list, LoadoutId loadout)
    {
        if (FindGroup(db, list, loadout) is not { } group) return new WabbajackModlistInstall(WabbajackModlistCardState.NotInstalled, null, false);

        // The enable toggle is the user's, not the install's: a finished list they switched off is still installed.
        var finished = group.Contains(WabbajackModlistLoadoutGroup.InstalledAt);
        var skipped = WabbajackSkippedFile.FindByGroup(db, group.WabbajackModlistLoadoutGroupId).Any();
        var state = finished && !skipped ? WabbajackModlistCardState.Installed : WabbajackModlistCardState.Incomplete;
        return new WabbajackModlistInstall(state, group.AsCollectionGroup().CollectionGroupId, finished);
    }

    /// <summary>
    /// The state now and after every revision that changes it. Each revision is read from its own snapshot, never from
    /// <c>connection.Db</c>, so one that lands while a group is being removed is read consistently. Emits on the
    /// database thread.
    /// </summary>
    public static IObservable<WabbajackModlistInstall> Observe(IConnection connection, LibraryFileId list, LoadoutId loadout)
        => connection.Revisions
            .Select(db => ReadInstall(db, list, loadout))
            .Prepend(ReadInstall(connection.Db, list, loadout))
            .DistinctUntilChanged();
}

/// <summary>Where a modlist stands in one loadout, the group that holds the answer, and whether the install ran to its end.</summary>
/// <param name="Finished">The install wrote <c>InstalledAt</c>; the files it skipped are a separate matter.</param>
public sealed record WabbajackModlistInstall(WabbajackModlistCardState State, CollectionGroupId? Group, bool Finished);
