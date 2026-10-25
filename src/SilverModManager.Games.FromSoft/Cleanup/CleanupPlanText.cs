namespace SilverModManager.Games.FromSoft.Cleanup;

/// <summary>The plan as lines of text, which the command line and the Health Check dialog both show.</summary>
public static class CleanupPlanText
{
    public static IReadOnlyList<string> Lines(CleanupPlan plan)
    {
        if (plan.IsEmpty) return ["Nothing to clean up: the game folder is as Steam installed it."];

        var lines = StepLines(plan).ToList();

        if (plan.IsBlocked) lines.Add("Nothing is applied while a step is blocked. Clear the attribute first (the Fix button asks for your password, or run the command shown), then plan again.");
        return lines;
    }

    /// <summary>The numbered steps alone, for a list the user confirms.</summary>
    public static IEnumerable<string> StepLines(CleanupPlan plan)
        => plan.Steps.Select((step, index) => $"{index + 1}. {step.Description}{(step.BlockedReason is { } reason ? $" (blocked: {reason})" : "")}");
}
