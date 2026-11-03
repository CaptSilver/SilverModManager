namespace SilverModManager.App.Mcp;

/// <summary>The tools that change something. A read-only server does not offer them.</summary>
public static class MutatingTools
{
    internal static ToolParam WabbajackModlistParam(bool required) => new(
        "modlist", "The modlist: its name or id as wabbajack_list shows them, or the file name of its .wabbajack in the library." + (required ? "" : " Leave out for whichever batch ran last."),
        Required: required);

    private static readonly ToolParam GroupParam = new(
        "group", "The group's name as loadout groups lists it, or its id from the JSON when two groups share a name.", Required: true);

    private const string UndoNote = " Undo with loadout_revert. Find the loadout with loadouts_list.";

    public static IReadOnlyList<McpTool> Create(McpToolContext context) =>
    [
        VerbTool.Create(context, "loadout_apply",
            "Applies a loadout to its game folder, the same as the Apply button: files are added, changed and removed on disk. " +
            "Stopping the call does not stop the apply in the app. Find the loadout with loadouts_list.",
            readOnly: false, ["loadout", "synchronize"], takesLoadout: true, context.Limits.ApplyTimeout,
            emptyText: args => $"Applied {args.String("loadout")}."),
        VerbTool.Create(context, "loadout_group_enable",
            "Switches a mod group on in a loadout, the same as its switch on the Library page. Groups a collection or the game's install controls are left alone and named. " +
            "A restore point is written first." + UndoNote,
            readOnly: false, ["loadout", "group", "enable"], takesLoadout: true, context.Limits.ReadTimeout, options: [GroupParam]),
        VerbTool.Create(context, "loadout_group_disable",
            "Switches a mod group off in a loadout; its files stay. Groups a collection or the game's install controls are left alone and named. " +
            "A restore point is written first." + UndoNote,
            readOnly: false, ["loadout", "group", "disable"], takesLoadout: true, context.Limits.ReadTimeout, options: [GroupParam]),
        VerbTool.Create(context, "loadout_group_priority_set",
            "Moves a mod group to an index in the loadout's priority order, as dragging it on the Load Order page does. Index 0 loses and the last index wins; an index past the end is clamped. " +
            "A restore point is written first. Read the order with loadout_priority_list." + UndoNote,
            readOnly: false, ["loadout", "group", "priority", "set"], takesLoadout: true, context.Limits.ReadTimeout,
            options:
            [
                GroupParam,
                new ToolParam("index", "The index to move the group to, 0 or more.", Required: true, Type: "integer"),
            ]),
        VerbTool.Create(context, "loadout_plugins_set",
            "Puts the named plugins in the order given and leaves the others where they are: the first keeps its place and each following one goes just after the one before. " +
            "Nothing is written when a name is unknown or locked. A restore point is written first. Read the order with loadout_plugins_list." + UndoNote,
            readOnly: false, ["loadout", "plugins", "set"], takesLoadout: true, context.Limits.ReadTimeout,
            options: [new ToolParam("order", "Plugin file names separated by ';' in the order they should load, for example 'a.esp;b.esp'.", Required: true)]),
        VerbTool.Create(context, "loadout_plugins_sort",
            "Sorts a loadout's plugins with LOOT's masterlist, as the Sort with LOOT button does, and reports the outcome. " +
            "It reads plugin headers and hashes files, so it can take a while. A restore point is written first." + UndoNote,
            readOnly: false, ["loadout", "plugins", "sort"], takesLoadout: true, context.Limits.SlowReadTimeout),
        VerbTool.Create(context, "loadout_revert",
            "Puts a loadout back to one of its restore points, undoing what was changed after it (group switches, priority and plugin order, removed files). " +
            "List the numbers with loadout_revisions. Find the loadout with loadouts_list.",
            readOnly: false, ["loadout", "revert"], takesLoadout: true, context.Limits.ReadTimeout,
            options: [new ToolParam("revision", "The revision number from loadout_revisions.", Required: true, Type: "integer", CliName: "revisionNumber")],
            emptyText: args => $"Reverted {args.String("loadout")} to revision {args.Int("revision")}."),
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
            "It needs the SilverModManager window to be open, because the batch goes on in the app and continues after a restart. Follow it with wabbajack_status. Find the modlists with wabbajack_list.",
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
