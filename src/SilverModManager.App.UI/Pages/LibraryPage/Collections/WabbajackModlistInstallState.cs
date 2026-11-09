using System.Reactive.Linq;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Games;
using SilverModManager.Wabbajack.Gallery;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.App.UI.Pages.LibraryPage.Collections;

/// <summary>Where a modlist stands in one loadout.</summary>
public enum WabbajackModlistCardState
{
    NotInstalled,

    /// <summary>Its group exists but the install did not finish, or files were skipped.</summary>
    Incomplete,

    Installed,

    /// <summary>A newer version of the list is installed, or the install was moved off this file by an update. The file is no longer an install to offer.</summary>
    Superseded,
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

    /// <summary>
    /// The group of <paramref name="loadout"/> that holds another release of the same gallery list as <paramref name="list"/>,
    /// when <paramref name="list"/> has no group there. Installing the file would put a second copy of the list beside it;
    /// updating that group to the file is the way to use it.
    /// </summary>
    public static WabbajackModlistLoadoutGroup.ReadOnly? FindOtherVersion(IDb db, LibraryFileId list, LoadoutId loadout)
    {
        var file = WabbajackModlistLibraryFile.Load(db, list.Value);
        if (!file.IsValid()) return null;

        foreach (var group in WabbajackModlistLoadoutGroup.All(db))
        {
            var current = group.Modlist;
            if (!current.IsValid() || current.Id == file.Id || !ModlistIdentity.SameList(current, file)) continue;
            if (group.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().LoadoutId == loadout) return group;
        }
        return null;
    }

    public static WabbajackModlistCardState Read(IDb db, LibraryFileId list, LoadoutId loadout) => ReadInstall(db, list, loadout).State;

    /// <summary>The state with the group it was read from, for callers that need to act on the group too.</summary>
    public static WabbajackModlistInstall ReadInstall(IDb db, LibraryFileId list, LoadoutId loadout)
    {
        if (FindGroup(db, list, loadout) is not { } group)
        {
            if (SupersededBy(db, list, loadout) is { } newer)
                return new WabbajackModlistInstall(WabbajackModlistCardState.Superseded, null, false, newer);

            return FindOtherVersion(db, list, loadout) is { } other
                ? new WabbajackModlistInstall(WabbajackModlistCardState.NotInstalled, null, false, OtherVersion: other.Modlist.Version, OtherGroup: other.Id)
                : new WabbajackModlistInstall(WabbajackModlistCardState.NotInstalled, null, false);
        }

        // The enable toggle is the user's, not the install's: a finished list they switched off is still installed.
        var finished = group.Contains(WabbajackModlistLoadoutGroup.InstalledAt);
        var skipped = WabbajackSkippedFile.SkipsThatKeepTheListOff(db, group.Id).Any();
        var state = finished && !skipped ? WabbajackModlistCardState.Installed : WabbajackModlistCardState.Incomplete;
        return new WabbajackModlistInstall(state, group.AsCollectionGroup().CollectionGroupId, finished);
    }

    /// <summary>
    /// The version of the list that replaced <paramref name="list"/>, or null when nothing did. Two things replace a
    /// file: an update that switched a group of <paramref name="loadout"/> away from it (the group's previous list), and
    /// a newer release of the same gallery list (same repository and machine URL, a later gallery date on both or else a
    /// higher numeric version) installed in any loadout of the same game. A file added from disk, with no gallery identity, is only replaced by the first.
    /// </summary>
    public static string? SupersededBy(IDb db, LibraryFileId list, LoadoutId loadout)
    {
        var file = WabbajackModlistLibraryFile.Load(db, list.Value);
        var here = Loadout.Load(db, loadout.Value);
        if (!file.IsValid() || !here.IsValid()) return null;

        var game = GameInstallMetadata.Game.TryGetValue(here.Installation, out var gameId) ? gameId : (GameId?)null;
        foreach (var group in WabbajackModlistLoadoutGroup.All(db))
        {
            var current = group.Modlist;
            if (!current.IsValid() || current.Id == file.Id) continue;

            // A loadout deleted while its rows are still being cleaned up has nothing to compare.
            var groupLoadout = group.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().Loadout;
            if (!groupLoadout.IsValid()) continue;
            if (groupLoadout.LoadoutId == loadout
                && WabbajackModlistLoadoutGroup.PreviousModlist.TryGetValue(group, out var previous) && previous == file.Id)
                return current.Version;

            if (game is null || !ModlistIdentity.SameList(current, file)) continue;
            if (!GameInstallMetadata.Game.TryGetValue(groupLoadout.Installation, out var groupGame) || groupGame != game) continue;
            if (ModlistIdentity.IsOlder(file, current)) return current.Version;
        }
        return null;
    }

    /// <summary>
    /// The file an update switched a group of <paramref name="loadout"/> away from <paramref name="list"/> to, or null.
    /// The group records the file it left, so this is an explicit fact about the list and safe to act on when a page
    /// opens.
    /// </summary>
    public static LibraryFileId? SwitchedTo(IDb db, LibraryFileId list, LoadoutId loadout)
    {
        foreach (var group in WabbajackModlistLoadoutGroup.FindByPreviousModlist(db, list.Value))
        {
            if (group.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().LoadoutId != loadout) continue;
            if (group.Modlist.IsValid()) return LibraryFileId.From(group.Modlist.Id);
        }
        return null;
    }

    /// <summary>
    /// The file whose page replaces the page of <paramref name="list"/> in <paramref name="loadout"/>, or null when this
    /// page stays: <see cref="SwitchedTo"/>, or else the file of another release of the same gallery list that holds the
    /// group once <paramref name="list"/> has lost its own, which is how the new file reads after an update was undone.
    /// A file that never held a group (<paramref name="heldGroupBefore"/> false) has nothing to lose: it reads the same
    /// way when it is a newer release opened beside an installed older one, a page the user chose, so it stays.
    /// </summary>
    public static LibraryFileId? SuccessorOf(IDb db, LibraryFileId list, LoadoutId loadout, bool heldGroupBefore = true)
    {
        if (SwitchedTo(db, list, loadout) is { } switched) return switched;
        if (!heldGroupBefore || FindGroup(db, list, loadout) is not null) return null;
        return FindOtherVersion(db, list, loadout) is { } other ? LibraryFileId.From(other.Modlist.Id) : null;
    }

    /// <summary>
    /// The state now and after every revision that changes it. Each revision is read from its own snapshot, never from
    /// <c>connection.Db</c>, so one that lands while a group is being removed is read consistently. Emits on the
    /// database thread.
    /// </summary>
    public static IObservable<WabbajackModlistInstall> Observe(IConnection connection, LibraryFileId list, LoadoutId loadout)
        // Deferred so the first value is the state when someone subscribes, not when the observable was made.
        => Observable.Defer(() => connection.Revisions
            .Select(db => ReadInstall(db, list, loadout))
            .Prepend(ReadInstall(connection.Db, list, loadout))
            .DistinctUntilChanged());
}

/// <summary>Where a modlist stands in one loadout, the group that holds the answer, and whether the install ran to its end.</summary>
/// <param name="Finished">The install wrote <c>InstalledAt</c>; the files it skipped are a separate matter.</param>
/// <param name="SupersededBy">The version that replaced the list, when <see cref="State"/> is <see cref="WabbajackModlistCardState.Superseded"/>.</param>
/// <param name="OtherVersion">The version of another release of this list that is installed in the loadout, while this file is not. <see cref="State"/> stays NotInstalled; the file must not be installed beside that release.</param>
/// <param name="OtherGroup">The group that holds <paramref name="OtherVersion"/>.</param>
public sealed record WabbajackModlistInstall(WabbajackModlistCardState State, CollectionGroupId? Group, bool Finished, string? SupersededBy = null, string? OtherVersion = null, EntityId? OtherGroup = null);
