namespace SilverModManager.App.Mcp;

/// <summary>The tools that change something. A read-only server does not offer them.</summary>
public static class MutatingTools
{
    internal static ToolParam WabbajackModlistParam(bool required) => new(
        "modlist", "The modlist: its name or id as wabbajack_list shows them, or the file name of its .wabbajack in the library." + (required ? "" : " Leave out for whichever batch ran last."),
        Required: required);

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
        VerbTool.Create(context, "wabbajack_download",
            "Starts downloading a Wabbajack modlist's archives in the running app, the same as the Download button on the modlist's page, " +
            "and returns once the batch is under way with how many archives download automatically, need the browser, need hand work or are already there. " +
            "The batch goes on in the app and continues after a restart. Follow it with wabbajack_status. Find the modlists with wabbajack_list.",
            readOnly: false, ["wabbajack", "download"], takesLoadout: false, context.Limits.SlowReadTimeout,
            options: [WabbajackModlistParam(required: true)]),
        VerbTool.Create(context, "wabbajack_cancel",
            "Stops the Wabbajack modlist download that is running. It is not resumed after a restart.",
            readOnly: false, ["wabbajack", "cancel"], takesLoadout: false, context.Limits.ReadTimeout),
        VerbTool.Create(context, "game_launch",
            "Applies a loadout and starts its game, the same as the Play button, and returns once the launch is under way, not when the game exits. " +
            "It needs the SilverModManager window to be open. Check app_status afterwards for the game process. Find the loadout with loadouts_list.",
            readOnly: false, ["loadout", "launch"], takesLoadout: true, context.Limits.SlowReadTimeout),
    ];
}
