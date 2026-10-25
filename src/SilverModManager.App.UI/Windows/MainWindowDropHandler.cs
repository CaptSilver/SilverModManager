using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Sdk.Library;
using SilverModManager.UI.Sdk;

namespace SilverModManager.App.UI.Windows;

/// <summary>What dropped files do in the app. Everything goes through the library; nothing is copied into a game folder.</summary>
public interface IMainWindowDropActions
{
    /// <summary>Adds a <c>.wabbajack</c> file with the library's add-local-file job. Throws if the library did not recognise it as a modlist.</summary>
    Task<LibraryFileId> AddModlist(AbsolutePath file, CancellationToken cancellationToken);

    /// <summary>Adds a mod archive with the library's add-local-file job.</summary>
    Task AddMod(AbsolutePath file, CancellationToken cancellationToken);

    void OpenModlist(LibraryFileId file);

    /// <summary>The loadout of the page that is open, or null when the window is not on a loadout.</summary>
    string? CurrentLoadoutName { get; }
}

/// <summary>Runs a <see cref="MainWindowDropPlan"/> and reports the outcome: one toast for what was added, one for everything that was refused or failed.</summary>
public sealed class MainWindowDropHandler(IMainWindowDropActions actions, IWindowNotificationService notifications, ILogger? logger = null)
{
    public async Task HandleAsync(IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        var plan = MainWindowDropPlan.Classify(paths);
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
        var text = $"Added {MainWindowDropPlan.ModCount(added)} to the library";
        return actions.CurrentLoadoutName is { } loadout ? $"{text}. Install them into {loadout} from its Library page." : text;
    }
}
