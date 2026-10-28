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
        VerbTool.Create(context, "settings_set",
            "Changes one app setting, the same as the Settings page, and says if a restart is needed for it to take effect. " +
            "Values are checked against the setting's type and rules. An empty value clears a text setting. " +
            "Find the keys with settings_list.",
            readOnly: false, ["settings", "set"], takesLoadout: false, context.Limits.ReadTimeout,
            options:
            [
                ReadOnlyTools.SettingKeyParam,
                new ToolParam("value", "The new value: true or false, a number, the name of an option, or text. It cannot start with '-'.", Required: true),
            ]),
        VerbTool.Create(context, "game_launch",
            "Applies a loadout and starts its game, the same as the Play button, and returns once the launch is under way, not when the game exits. " +
            "It needs the SilverModManager window to be open. Check app_status afterwards for the game process. Find the loadout with loadouts_list.",
            readOnly: false, ["loadout", "launch"], takesLoadout: true, context.Limits.SlowReadTimeout),
    ];
}
