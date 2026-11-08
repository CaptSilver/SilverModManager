using ReactiveUI;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Errors;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;
using SilverModManager.UI.Sdk.Icons;
using SilverModManager.Wabbajack.Gallery;
using SilverModManager.Wabbajack.Models;
using SilverModManager.Wabbajack.Update;

namespace SilverModManager.App.UI.Pages.Collections.Update;

/// <summary>
/// The more-menu entries that belong to a Wabbajack list's update besides "Update to…": Undo and Finish while a
/// restore point is recorded, and the picker for a list whose gallery entry is not known.
/// </summary>
public static class WabbajackUpdateMenuItems
{
    public static readonly ButtonDefinitionId FinishAndDelete = ButtonDefinitionId.From("finish-update-delete-old");

    /// <summary>Whether the group has a restore point from an update that has not been finished.</summary>
    public static bool HasRestorePoint(IDb db, EntityId group)
        => WabbajackModlistLoadoutGroup.Load(db, group) is { } loaded && loaded.IsValid() && WabbajackModlistLoadoutGroup.LastUpdateRestorePoint.TryGetValue(loaded, out _);

    public static IContextMenuItem UndoUpdate(IServiceProvider services, ICollectionPageHost host, EntityId group) => new ContextMenuItem
    {
        Header = Language.CollectionUpdate_Undo,
        Icon = IconValues.Undo,
        Command = ReactiveCommand.CreateFromTask(() => ConfirmAndUndo(services, host, group)),
    };

    public static IContextMenuItem FinishUpdate(IServiceProvider services, ICollectionPageHost host, EntityId group) => new ContextMenuItem
    {
        Header = Language.CollectionUpdate_Menu_Finish,
        Icon = IconValues.Check,
        Command = ReactiveCommand.CreateFromTask(() => ConfirmAndFinish(services, host, group)),
    };

    /// <summary>"Which gallery entry is this list?": one pick among the entries that could be the list, written as its identity.</summary>
    public static IContextMenuItem PickGalleryEntry(
        IServiceProvider services, ICollectionPageHost host, WabbajackModlistLibraryFile.ReadOnly file, IReadOnlyList<GalleryEntry> candidates) => new ContextMenuItem
    {
        Header = Language.CollectionUpdate_Pick_Title,
        Icon = IconValues.Help,
        Command = ReactiveCommand.CreateFromTask(() => Pick(services, host, file.Id, candidates)),
    };

    public static string UndoneText(string name, string restored)
        => string.Format(Language.CollectionUpdate_Undone, MarkdownText.InlineMidLine(name), MarkdownText.InlineMidLine(restored));

    public static string FinishedText(string name) => string.Format(Language.CollectionUpdate_Finished, MarkdownText.Inline(name));

    private static async Task ConfirmAndUndo(IServiceProvider services, ICollectionPageHost host, EntityId groupId)
    {
        var connection = services.GetRequiredService<IConnection>();
        var group = WabbajackModlistLoadoutGroup.Load(connection.Db, groupId);
        var name = group.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().Name;
        var dialog = DialogFactory.CreateStandardDialog(
            string.Format(Language.CollectionUpdate_Undo_Title, name),
            new StandardDialogParameters { Text = string.Format(Language.CollectionUpdate_Undo_Text, name) },
            [DialogStandardButtons.Cancel, new DialogButtonDefinition(Language.CollectionUpdate_Undo, ButtonDefinitionId.Accept, ButtonAction.Accept, ButtonStyling.Destructive)]);
        if ((await host.WindowManager.ShowDialog(dialog, DialogWindowType.Modal)).ButtonId != ButtonDefinitionId.Accept) return;

        var notifications = services.GetRequiredService<IWindowNotificationService>();
        try
        {
            var restored = await WabbajackCollectionUpdate.Undo(services, group, CancellationToken.None);
            notifications.ShowToast(UndoneText(name, restored), ToastNotificationVariant.Success);
        }
        catch (Exception e) when (!TimeoutDetection.IsRealCancellation(e))
        {
            await services.GetRequiredService<IErrorPresenter>().Report(e, "undoing the update");
        }
    }

    private static async Task ConfirmAndFinish(IServiceProvider services, ICollectionPageHost host, EntityId groupId)
    {
        var connection = services.GetRequiredService<IConnection>();
        var finish = services.GetRequiredService<ModlistUpdateFinish>();
        var group = WabbajackModlistLoadoutGroup.Load(connection.Db, groupId);
        var name = group.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().Name;
        var plan = finish.Plan(group);

        var owned = plan.OldVersion.OwnedArchives.Count;
        var text = Language.CollectionUpdate_Finish_Text;
        if (owned > 0) text += "\n" + string.Format(Language.CollectionUpdate_Finish_OldArchives, owned.ToString("N0"), plan.OldVersion.OwnedSize);
        List<DialogButtonDefinition> buttons =
        [
            DialogStandardButtons.Cancel,
            new(Language.CollectionUpdate_Menu_Finish, ButtonDefinitionId.Accept, ButtonAction.Accept, ButtonStyling.Primary),
        ];
        if (owned > 0)
            buttons.Add(new(string.Format(Language.CollectionUpdate_Finish_DeleteButton, owned.ToString("N0"), plan.OldVersion.OwnedSize), FinishAndDelete, ButtonAction.Accept, ButtonStyling.Destructive));

        var dialog = DialogFactory.CreateStandardDialog(string.Format(Language.CollectionUpdate_Finish_Title, name), new StandardDialogParameters { Text = text }, [.. buttons]);
        var answer = (await host.WindowManager.ShowDialog(dialog, DialogWindowType.Modal)).ButtonId;
        if (answer != ButtonDefinitionId.Accept && answer != FinishAndDelete) return;

        var notifications = services.GetRequiredService<IWindowNotificationService>();
        try
        {
            await finish.Finish(group, deleteOldArchives: answer == FinishAndDelete, CancellationToken.None);
            notifications.ShowToast(FinishedText(name), ToastNotificationVariant.Success);
        }
        catch (Exception e) when (!TimeoutDetection.IsRealCancellation(e))
        {
            await services.GetRequiredService<IErrorPresenter>().Report(e, "finishing the update");
        }
    }

    private static async Task Pick(IServiceProvider services, ICollectionPageHost host, EntityId fileId, IReadOnlyList<GalleryEntry> candidates)
    {
        var buttons = candidates
            .Select((entry, index) => new DialogButtonDefinition(
                string.Format(Language.CollectionUpdate_Pick_Candidate, entry.Title, entry.Author, entry.Repository),
                ButtonDefinitionId.From($"pick-{index}"), ButtonAction.Accept, ButtonStyling.Primary))
            .Prepend(DialogStandardButtons.Cancel)
            .ToArray();
        var dialog = DialogFactory.CreateStandardDialog(Language.CollectionUpdate_Pick_Title, new StandardDialogParameters { Text = Language.CollectionUpdate_Pick_Text }, buttons);
        var answer = (await host.WindowManager.ShowDialog(dialog, DialogWindowType.Modal)).ButtonId;

        var picked = candidates.Where((_, index) => answer == ButtonDefinitionId.From($"pick-{index}")).FirstOrDefault();
        if (picked is null) return;

        var file = WabbajackModlistLibraryFile.Load(services.GetRequiredService<IConnection>().Db, fileId);
        if (file.IsValid()) await services.GetRequiredService<ModlistIdentityBackfill>().Assign(file, picked);
    }
}
