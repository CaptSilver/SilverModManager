namespace SilverModManager.Games.FromSoft.Cleanup;

/// <summary>The plan as lines of text, which the command line and the Health Check dialog both show.</summary>
public static class CleanupPlanText
{
    public static IReadOnlyList<string> Lines(CleanupPlan plan)
    {
        if (plan.IsEmpty) return ["Nothing to clean up: the game folder is as Steam installed it."];

        var lines = plan.Steps
            .Select((step, index) => $"{index + 1}. {step.Description}{(step.BlockedReason is { } reason ? $" (blocked: {reason})" : "")}")
            .ToList();

        if (plan.IsBlocked) lines.Add("Nothing is applied while a step is blocked. Do what the command step says, then plan again.");
        return lines;
    }
}
