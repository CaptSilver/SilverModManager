namespace SilverModManager.App.Mcp;

/// <summary>The tools that change something. A read-only server does not offer them.</summary>
public static class MutatingTools
{
    public static IReadOnlyList<McpTool> Create(McpToolContext context) =>
    [
        VerbTool.Create(context, "loadout_apply",
            "Applies a loadout to its game folder, the same as the Apply button: files are added, changed and removed on disk. " +
            "Stopping the call does not stop the apply in the app. Find the loadout with loadouts_list.",
            readOnly: false, ["loadout", "synchronize"], takesLoadout: true, context.Limits.ApplyTimeout,
            emptyText: args => $"Applied {args.String("loadout")}."),
        VerbTool.Create(context, "game_launch",
            "Applies a loadout and starts its game, the same as the Play button, and returns once the launch is under way, not when the game exits. " +
            "It needs the SilverModManager window to be open. Check app_status afterwards for the game process. Find the loadout with loadouts_list.",
            readOnly: false, ["loadout", "launch"], takesLoadout: true, context.Limits.SlowReadTimeout),
    ];
}
