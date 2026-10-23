using SilverModManager.Abstractions.Cli;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.Games.FromSoft.Cleanup;

/// <summary>
/// A game's cleanup verb: shows what is in the game folder and the steps that would restore it, and with
/// <c>--apply --yes</c> runs them and shows what changed. The renderer only prints, so the confirmation is the
/// <c>--yes</c> of a second run that follows reading the first one's list.
/// </summary>
public sealed class CleanupCommand(CleanupService service)
{
    private const int Failed = 1;

    public async Task<int> RunAsync(IRenderer renderer, Loadout.ReadOnly loadout, bool plan, bool apply, bool yes)
    {
        if (plan == apply)
        {
            await renderer.TextLine("Pass exactly one of --plan and --apply.");
            return Failed;
        }

        CleanupPlan steps;
        IReadOnlyList<InventoryEntry> before;
        try
        {
            steps = service.Plan(loadout);
            before = service.Inventory(loadout);
        }
        catch (InvalidOperationException e)
        {
            await renderer.TextLine(e.Message);
            return Failed;
        }

        await renderer.TextLine("Game folder before the cleanup:");
        foreach (var line in GameFolderInventory.Describe(before)) await renderer.TextLine("  " + line);
        await renderer.TextLine("");
        await renderer.TextLine("Steps:");
        foreach (var line in CleanupPlanText.Lines(steps)) await renderer.TextLine("  " + line);
        if (plan) return 0;

        if (steps.IsBlocked)
        {
            await renderer.TextLine("Nothing was changed. Once the command above has been run, run this again.");
            return Failed;
        }

        if (!steps.Steps.Any(s => s.Acts)) return 0;

        if (!yes)
        {
            await renderer.TextLine("Nothing was changed. Read the steps above, then run this again with --yes to apply them.");
            return Failed;
        }

        var result = await service.ApplyAsync(loadout, steps);
        foreach (var done in result.Done) await renderer.TextLine("Done: " + done.Description);
        if (result.Failure is { } failure) await renderer.TextLine("Stopped: " + failure);

        var after = service.Inventory(loadout);
        await renderer.TextLine("");
        await renderer.TextLine("Game folder after the cleanup:");
        foreach (var line in GameFolderInventory.Describe(after)) await renderer.TextLine("  " + line);
        await renderer.TextLine("");
        await renderer.TextLine("Changes:");
        foreach (var line in GameFolderInventory.Diff(before, after)) await renderer.TextLine("  " + line);

        return result.Succeeded ? 0 : Failed;
    }
}
