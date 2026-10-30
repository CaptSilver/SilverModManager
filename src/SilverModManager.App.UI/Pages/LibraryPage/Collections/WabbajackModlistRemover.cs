using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.App.UI.Helpers;
using SilverModManager.UI.Sdk;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Library;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.App.UI.Pages.LibraryPage.Collections;

/// <summary>An archive the removal leaves in the library, and why.</summary>
public sealed record KeptArchive(string Name, string Reason);

/// <summary>What removing a modlist takes with it, worked out before anything is deleted.</summary>
/// <param name="Groups">The list's installs, one per loadout it was installed into.</param>
/// <param name="LoadoutNames">The loadouts those installs are in.</param>
/// <param name="OwnedArchives">Library files only this list uses: fetched for it, named by no other list and installed nowhere.</param>
public sealed record ModlistRemovalPlan(
    LibraryFileId Modlist,
    string Name,
    IReadOnlyList<CollectionGroupId> Groups,
    IReadOnlyList<string> LoadoutNames,
    IReadOnlyList<LibraryItemId> OwnedArchives,
    Size OwnedSize,
    IReadOnlyList<KeptArchive> Kept);

/// <summary>
/// Removes a modlist from the library with its installs and, if asked, the archives the app fetched for it that
/// nothing else uses. An archive stays when another list names it, or when the user installed it into a loadout.
/// </summary>
public sealed class WabbajackModlistRemover(IServiceProvider provider)
{
    private readonly IConnection _connection = provider.GetRequiredService<IConnection>();

    public ModlistRemovalPlan Plan(LibraryFileId modlist)
    {
        var db = _connection.Db;
        var name = WabbajackModlistLibraryFile.Load(db, modlist.Value).Name;

        var groups = new List<CollectionGroupId>();
        var loadouts = new List<string>();
        foreach (var group in WabbajackModlistLoadoutGroup.FindByModlist(db, modlist.Value))
        {
            var item = group.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem();
            groups.Add(CollectionGroupId.From(group.Id));
            loadouts.Add(item.Loadout.Name);
        }

        var owned = new List<LibraryItemId>();
        var ownedSize = Size.Zero;
        var kept = new List<KeptArchive>();
        foreach (var row in WabbajackModlistArchive.FindByModlist(db, modlist.Value))
        {
            foreach (var fetched in row.FetchedFiles)
            {
                var file = LibraryFile.Load(db, fetched.Id);
                if (!file.IsValid()) continue;
                // A member of one of the user's archives is not ours to delete, whatever linked it.
                if (LibraryArchiveLocator.IsArchiveMember(db, file.Id)) continue;

                var otherList = OtherListNaming(db, row, modlist);
                if (otherList is not null)
                    kept.Add(new KeptArchive(row.Name, $"used by {otherList}"));
                else if (LibraryLinkedLoadoutItem.FindByLibraryItem(db, fetched.Id).Any())
                    kept.Add(new KeptArchive(row.Name, "installed in a loadout"));
                else
                {
                    owned.Add(LibraryItemId.From(file.Id));
                    ownedSize += file.Size;
                }
            }
        }

        return new ModlistRemovalPlan(modlist, name, groups, loadouts, owned, ownedSize, kept);
    }

    private static string? OtherListNaming(IDb db, WabbajackModlistArchive.ReadOnly row, LibraryFileId modlist)
    {
        foreach (var other in WabbajackModlistArchive.FindByIdentity(db, row.Identity))
        {
            if (other.ModlistId.Value == modlist.Value) continue;
            return WabbajackModlistLibraryFile.Load(db, other.ModlistId.Value) is { } list && list.IsValid() ? list.Name : "another modlist";
        }
        return null;
    }

    /// <summary>
    /// Passes the row's fetched file to the rows of other lists that name the same archive, so the file does not turn
    /// into a top-level Mods entry while one of them still needs it.
    /// </summary>
    private void HandOver(ITransaction tx, WabbajackModlistArchive.ReadOnly row)
    {
        foreach (var other in WabbajackModlistArchive.FindByIdentity(_connection.Db, row.Identity))
        {
            if (other.ModlistId.Value == row.ModlistId.Value || other.FetchedFiles.Any()) continue;
            foreach (var fetched in row.FetchedFiles) tx.Add(other.Id, WabbajackModlistArchive.FetchedFiles, fetched.Id);
        }
    }

    /// <summary>
    /// Deletes the list's installs first (the group before its metadata, as removing a collection does), then the
    /// chosen archives, the list's archive rows and the list's file.
    /// </summary>
    public async Task Remove(ModlistRemovalPlan plan, bool deleteArchives, IWorkspaceController workspaceController)
    {
        var loadoutManager = provider.GetRequiredService<ILoadoutManager>();
        var notifications = provider.GetRequiredService<IWindowNotificationService>();
        foreach (var group in plan.Groups)
            await CollectionDeleteHelpers.DeleteCollectionAsync(group, loadoutManager, workspaceController, _connection, notifications);

        var library = provider.GetRequiredService<ILibraryService>();
        var db = _connection.Db;
        if (deleteArchives)
        {
            var archives = plan.OwnedArchives.Select(id => LibraryItem.Load(db, id.Value)).Where(item => item.IsValid()).ToArray();
            if (archives.Length > 0) await library.RemoveLibraryItems(archives);
        }

        using (var tx = _connection.BeginTransaction())
        {
            foreach (var row in WabbajackModlistArchive.FindByModlist(_connection.Db, plan.Modlist.Value))
            {
                HandOver(tx, row);
                tx.Delete(row.Id, recursive: false);
            }
            await tx.Commit();
        }

        var list = LibraryItem.Load(_connection.Db, plan.Modlist.Value);
        if (list.IsValid()) await library.RemoveLibraryItems([list]);
    }
}
