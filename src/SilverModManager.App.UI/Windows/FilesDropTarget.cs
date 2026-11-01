using Avalonia.Input;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.App.UI.Controls.DragAndDrop;
using SilverModManager.UI.Sdk;

namespace SilverModManager.App.UI.Windows;

/// <summary>
/// The window's drop target for files from other apps: says what they turn into while they are dragged, then runs the
/// plan and reports the outcome with one toast for what was added and one for everything refused or failed.
/// </summary>
public sealed class FilesDropTarget(IMainWindowDropActions actions, IWindowNotificationService notifications, ILogger? logger) : IDropTarget
{
    public DropVerdict Inspect(DragPayload payload)
    {
        if (payload is not DragPayload.DroppedFiles files) return new DropVerdict.Ignore();

        var plan = MainWindowDropPlan.Classify(files.Paths);
        return plan.Items.Any(static item => item.Kind != DropKind.Refused)
            ? new DropVerdict.Accept(DragDropEffects.Copy, plan.OverlayText)
            : new DropVerdict.Refuse(plan.OverlayText);
    }

    public async Task DropAsync(DragPayload payload, CancellationToken cancellationToken)
    {
        if (payload is not DragPayload.DroppedFiles files) return;

        var plan = MainWindowDropPlan.Classify(files.Paths);
        var problems = plan.Refused.Select(static item => $"{item.FileName}: {item.Reason}").ToList();

        foreach (var item in plan.Modlists)
        {
            try
            {
                var file = await actions.AddModlist(FileSystem.Shared.FromUnsanitizedFullPath(item.Path), cancellationToken);
                actions.OpenModlist(file);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger?.LogWarning(e, "Could not open the dropped modlist '{File}'", item.Path);
                problems.Add($"{item.FileName}: {e.Message}");
            }
        }

        var added = 0;
        foreach (var item in plan.Mods)
        {
            try
            {
                await actions.AddMod(FileSystem.Shared.FromUnsanitizedFullPath(item.Path), cancellationToken);
                added++;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger?.LogWarning(e, "Could not add the dropped file '{File}'", item.Path);
                problems.Add($"{item.FileName}: {e.Message}");
            }
        }

        if (added > 0) notifications.ShowToast(AddedText(added), ToastNotificationVariant.Success);
        if (problems.Count > 0)
            notifications.ShowToast($"Couldn't use {(problems.Count == 1 ? "1 file" : $"{problems.Count} files")}. {string.Join(" ", problems)}", ToastNotificationVariant.Failure, TimeSpan.FromSeconds(12));
    }

    // The toast service has no working action buttons, so the toast says where to install instead of offering a button.
    private string AddedText(int added)
    {
        var text = $"Added {ModCount.Text(added)} to the library";
        return actions.CurrentLoadoutName is { } loadout ? $"{text}. Install them into {loadout} from its Library page." : text;
    }
}
