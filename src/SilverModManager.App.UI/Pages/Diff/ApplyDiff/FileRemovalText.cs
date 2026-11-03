using System.Text;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Sdk.Games;

namespace SilverModManager.App.UI.Pages.Diff.ApplyDiff;

/// <summary>
/// The words of the dialog that asks the user to confirm removing files from the loadout. Pure, so the wording can be
/// tested without a window.
/// </summary>
public static class FileRemovalText
{
    /// <summary>Above this many rows the dialog lists mods with a file count instead of every file.</summary>
    public const int MaxListedRows = 20;

    private const string ExternalChangesName = "External Changes";

    /// <summary>Builds the dialog text for the rows <paramref name="plan"/> would remove.</summary>
    public static string For(FileRemovalPlan plan)
    {
        var rows = plan.Removable.Concat(plan.Locked).ToArray();
        var paragraphs = new List<string> { "These files leave the loadout:\n\n" + List(rows) };

        var blocked = plan.Blocked.Select(r => r.Blocker).OfType<string>().Distinct().ToArray();
        if (blocked.Length > 0)
            paragraphs.Add(string.Join('\n', blocked));

        paragraphs.Add("The next Apply deletes the ones that are in the game folder, or puts the game's own version back where a mod had replaced it. The mods stay in the loadout.");

        var external = rows.Where(r => r.IsExternalChange).Select(r => Show(r.Path)).Distinct().ToArray();
        if (external.Length > 0)
            paragraphs.Add($"{Join(external)} {(external.Length == 1 ? "is a file" : "are files")} you put in the game folder yourself. The next Apply deletes {(external.Length == 1 ? "it" : "them")} from disk; {(external.Length == 1 ? "it was" : "they were")} archived when the app found {(external.Length == 1 ? "it" : "them")}, so `loadout revert` brings {(external.Length == 1 ? "it" : "them")} back.");

        foreach (var locked in plan.Locked.GroupBy(r => r.CollectionName).OrderBy(g => g.Key, StringComparer.Ordinal))
            paragraphs.Add($"{locked.First().LockReason}. Removing files from its mods is a local edit: reinstalling or resuming the list puts them back.");

        return string.Join("\n\n", paragraphs);
    }

    private static string List(FileRemovalRow[] rows)
    {
        var sb = new StringBuilder();
        if (rows.Length > MaxListedRows)
        {
            var perGroup = rows
                .GroupBy(r => (Name: GroupLabel(r), r.CollectionName))
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key.Name, StringComparer.Ordinal);
            foreach (var group in perGroup)
                sb.Append(group.Key.Name).Append(InCollection(group.Key.CollectionName)).Append(" — ").Append(group.Count()).Append(group.Count() == 1 ? " file" : " files").Append('\n');
        }
        else
        {
            foreach (var row in rows.OrderBy(r => r.Path.Path.ToString(), StringComparer.Ordinal).ThenBy(r => r.GroupName, StringComparer.Ordinal))
            {
                sb.Append(Show(row.Path)).Append(" — ").Append(GroupLabel(row)).Append(InCollection(row.CollectionName));
                if (row.IsDisabled) sb.Append(" (switched off)");
                else if (!row.IsWinner) sb.Append(" (not the file the game would get)");
                sb.Append('\n');
            }
        }

        return sb.ToString().TrimEnd('\n');
    }

    private static string GroupLabel(FileRemovalRow row) => row.IsExternalChange ? ExternalChangesName : row.GroupName;

    private static string InCollection(string? collection) => collection is null ? "" : $", in {collection}";

    private static string Show(GamePath path) => path.LocationId == LocationId.Game ? path.Path.ToString() : path.ToString();

    private static string Join(string[] items) => items.Length == 1
        ? items[0]
        : string.Join(", ", items[..^1]) + " and " + items[^1];
}
