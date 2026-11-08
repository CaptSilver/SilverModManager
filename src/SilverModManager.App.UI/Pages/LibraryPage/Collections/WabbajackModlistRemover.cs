using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Helpers;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Library;

namespace SilverModManager.App.UI.Pages.LibraryPage.Collections;

/// <summary>
/// Removes a modlist from the library with its installs and, if asked, the archives the app fetched for it that
/// nothing else uses. An archive stays when another list names it, or when the user installed it into a loadout. A
/// list that an install was updated away from and not yet finished is refused: removing it would delete that install.
/// <see cref="ConfirmAndRemove"/> is the one dialog for it, shown by the Library card and by the left menu's pending entry.
/// </summary>
public sealed class WabbajackModlistRemover(IServiceProvider provider)
{
    private readonly IConnection _connection = provider.GetRequiredService<IConnection>();
    private readonly ModlistRemovalPlanner _planner = provider.GetRequiredService<ModlistRemovalPlanner>();

    /// <summary>The button that also removes the archives only this list uses.</summary>
    public static readonly ButtonDefinitionId RemoveWithArchives = ButtonDefinitionId.From("remove-with-archives");

    /// <summary>Works out the plan, shows what it takes with it and removes only if the user accepts.</summary>
    public async Task ConfirmAndRemove(IWindowManager windowManager, IWorkspaceController workspaceController, LibraryFileId modlist)
    {
        var plan = Plan(modlist);
        ModlistRemovalPlanner.EnsureNoUnfinishedUpdates(plan);
        var dialog = DialogFactory.CreateStandardDialog(
            title: $"Remove '{plan.Name}' from the library?",
            new StandardDialogParameters { Text = RemovalText(plan) },
            buttonDefinitions: RemovalButtons(plan));
        var result = await windowManager.ShowDialog(dialog, DialogWindowType.Modal);
        if (result.ButtonId != RemoveWithArchives && result.ButtonId != ButtonDefinitionId.Accept) return;

        await Remove(plan, deleteArchives: result.ButtonId == RemoveWithArchives, workspaceController);
    }

    internal static string RemovalText(ModlistRemovalPlan plan)
    {
        var text = new System.Text.StringBuilder();
        if (plan.LoadoutNames.Count > 0)
            text.AppendLine($"It is installed in {string.Join(", ", plan.LoadoutNames)}; those installs are removed too.");
        if (plan.OwnedArchives.Count > 0)
            text.AppendLine($"{plan.OwnedArchives.Count:N0} archives ({plan.OwnedSize}) were downloaded only for this modlist.");
        if (plan.Kept.Count > 0)
        {
            text.AppendLine("These stay in the library:");
            foreach (var kept in plan.Kept.Take(10)) text.AppendLine($"  {kept.Name}: {kept.Reason}");
            if (plan.Kept.Count > 10) text.AppendLine($"  and {plan.Kept.Count - 10:N0} more");
        }
        return text.Length == 0 ? "The modlist file is removed from the library." : text.ToString().TrimEnd();
    }

    private static DialogButtonDefinition[] RemovalButtons(ModlistRemovalPlan plan)
    {
        var keep = new DialogButtonDefinition(plan.OwnedArchives.Count > 0 ? "Remove list, keep archives" : "Remove", ButtonDefinitionId.Accept, ButtonAction.Accept, ButtonStyling.Destructive);
        return plan.OwnedArchives.Count == 0
            ? [DialogStandardButtons.Cancel, keep]
            : [DialogStandardButtons.Cancel, keep, new DialogButtonDefinition($"Remove list and {plan.OwnedArchives.Count:N0} archives ({plan.OwnedSize})", RemoveWithArchives, ButtonAction.Accept, ButtonStyling.Destructive)];
    }

    public ModlistRemovalPlan Plan(LibraryFileId modlist) => _planner.Plan(modlist);

    /// <summary>
    /// Deletes the list's installs first (the group before its metadata, as removing a collection does), then the
    /// chosen archives, the list's archive rows and the list's file.
    /// </summary>
    public async Task Remove(ModlistRemovalPlan plan, bool deleteArchives, IWorkspaceController workspaceController)
    {
        // Before the installs are deleted: the planner refuses too, but only after they would be gone.
        ModlistRemovalPlanner.EnsureNoUnfinishedUpdates(plan);
        var loadoutManager = provider.GetRequiredService<ILoadoutManager>();
        var notifications = provider.GetRequiredService<IWindowNotificationService>();
        foreach (var group in plan.Groups)
            await CollectionDeleteHelpers.DeleteCollectionAsync(group, loadoutManager, workspaceController, _connection, notifications);

        await _planner.RemoveFromLibrary(plan, deleteArchives);
    }
}
