using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.App.UI.Pages.LibraryPage.Collections;

/// <summary>Where a modlist stands in one loadout.</summary>
public enum WabbajackModlistCardState
{
    NotInstalled,

    /// <summary>Its group exists but the install did not finish: still disabled, or files were skipped.</summary>
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

    public static WabbajackModlistCardState Read(IDb db, LibraryFileId list, LoadoutId loadout)
    {
        if (FindGroup(db, list, loadout) is not { } group) return WabbajackModlistCardState.NotInstalled;

        var item = group.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem();
        var finished = group.Contains(WabbajackModlistLoadoutGroup.InstalledAt) && item.IsEnabled();
        var skipped = WabbajackSkippedFile.FindByGroup(db, group.WabbajackModlistLoadoutGroupId).Any();
        return finished && !skipped ? WabbajackModlistCardState.Installed : WabbajackModlistCardState.Incomplete;
    }
}
