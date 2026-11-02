using System.Reactive.Linq;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Pages.LibraryPage.Collections;
using SilverModManager.App.UI.Pages.Wabbajack;
using SilverModManager.App.UI.Windows;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;
using SilverModManager.Wabbajack.Install;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.App.UI.Pages.Collections;

/// <summary>
/// Installs an installed modlist again: removes what the install put in the loadout, then starts the install through
/// the Downloads tab's model, the way the Install button does, so the header and the tab follow the new run. The
/// modlist's group and the archives in the library stay, so nothing is downloaded again.
/// </summary>
/// <param name="loadout">The loadout the list is installed in, read when the item runs.</param>
public sealed class WabbajackModlistReinstaller(
    IConnection connection,
    ILoadoutManager loadouts,
    IErrorPresenter errors,
    ModlistInstallRuns runs,
    IWabbajackModlistViewModel downloads,
    LibraryFileId file,
    Func<LoadoutId?> loadout)
{
    /// <summary>
    /// <see cref="Run"/> for a menu item: a refusal or a failure is shown to the user instead of being thrown into a
    /// command nobody observes. Never throws.
    /// </summary>
    public async Task Reinstall(ICollectionPageHost host)
    {
        try
        {
            await Run(host);
        }
        catch (OperationCanceledException)
        {
            // Cancelled from the Jobs list; the page shows it.
        }
        catch (Exception e)
        {
            await errors.Report(e, "reinstalling the modlist");
        }
    }

    /// <summary>
    /// Refuses before anything is removed, asks the user, removes the group's contents and starts the install.
    /// Returns when the install ends; a cancelled dialog changes nothing.
    /// </summary>
    /// <exception cref="ModlistReinstallRefusedException">The list is not installed in the loadout, or its install would not start.</exception>
    /// <exception cref="InstallAlreadyRunningException">An install into the loadout has not finished.</exception>
    public async Task Run(ICollectionPageHost host, CancellationToken cancellationToken = default)
    {
        var target = loadout() ?? throw new ModlistReinstallRefusedException("The modlist is not installed in a loadout yet.");
        var group = WabbajackModlistInstallState.FindGroup(connection.Db, file, target)
            ?? throw new ModlistReinstallRefusedException("The modlist is not installed in this loadout.");
        var groupId = CollectionGroupId.From(group.Id);
        var name = LoadoutItem.Load(connection.Db, groupId).Name;

        runs.EnsureNoneInto(target);

        // The install must be able to start before anything is removed: the check also finds an archive that left the library.
        await downloads.PrepareInstallAsync(cancellationToken);
        if (!downloads.CanInstall) throw new ModlistReinstallRefusedException($"'{name}' cannot be installed again yet. {downloads.InstallBlockedReason}");

        if (!await Confirm(host, name)) return;

        // A run may have started while the dialog was open.
        runs.EnsureNoneInto(target);
        await RemoveContents(groupId);
        await downloads.InstallCommand.Execute();
    }

    private static async Task<bool> Confirm(ICollectionPageHost host, string name)
    {
        var dialog = DialogFactory.CreateStandardDialog(
            $"Reinstall '{name}'?",
            new StandardDialogParameters
            {
                Text = "Everything the install put in the loadout is removed and installed again from the archives in your library. Nothing is downloaded.",
            },
            [
                DialogStandardButtons.Cancel,
                new DialogButtonDefinition("Reinstall", ButtonDefinitionId.Accept, ButtonAction.Accept, ButtonStyling.Primary),
            ]);
        var result = await host.WindowManager.ShowDialog(dialog, DialogWindowType.Modal);
        return result.ButtonId == ButtonDefinitionId.Accept;
    }

    // The mods' groups go the way a removed mod does (priorities, rules and pins with them). The modlist's own group stays.
    private async Task RemoveContents(CollectionGroupId group)
    {
        var children = LoadoutItem.FindByParent(connection.Db, group).ToArray();
        var childGroups = children.OfTypeLoadoutItemGroup().Select(child => child.LoadoutItemGroupId).ToArray();
        if (childGroups.Length > 0) await loadouts.RemoveItems(childGroups);

        using var tx = connection.BeginTransaction();
        foreach (var item in LoadoutItem.FindByParent(connection.Db, group)) tx.Delete(item.Id, recursive: true);
        // The list is not installed until the new run ends; with InstalledAt left in place an install that fails to
        // start would leave an empty group that reads as installed and offers no Resume. The group is disabled the way
        // a first install creates it, so a failed or cancelled run never leaves a half-filled group enabled for the
        // next apply; the install's finish step enables it again.
        if (!LoadoutItem.Load(connection.Db, group).IsDisabled)
            tx.Add(group, LoadoutItem.Disabled, Null.Instance);
        var modlistGroup = WabbajackModlistLoadoutGroup.Load(connection.Db, group);
        if (modlistGroup.InstalledAt is { HasValue: true } installedAt)
            tx.Retract(group, WabbajackModlistLoadoutGroup.InstalledAt, installedAt.Value);
        await tx.Commit();
    }
}
