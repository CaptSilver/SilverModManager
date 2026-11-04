using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Cli;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.DataModel.CommandLine.Verbs;

/// <summary>
/// The load order verbs: the mod priority order every game has, and the plugin order of games that keep one. Both go
/// through the sort order varieties the Load Order page uses. A verb that changes an order writes a restore point
/// first, so <c>loadout revert</c> undoes it.
/// </summary>
internal static class LoadoutOrderVerbs
{
    private const string NoPluginOrder = "This game has no plugin order.";
    private const string LogCategory = "SilverModManager.DataModel.CommandLine.Verbs.LoadoutOrderVerbs";

    // The "loadout", "loadout group priority" and "loadout plugins" modules come from AddLoadoutManagementVerbs.
    public static IServiceCollection AddLoadoutOrderVerbs(this IServiceCollection services) => services
        .AddVerb(() => ListPriority)
        .AddVerb(() => GetGroupPriority)
        .AddVerb(() => SetGroupPriority)
        .AddVerb(() => ListPlugins)
        .AddVerb(() => SetPluginOrder)
        .AddVerb(() => SortPlugins);

    private sealed record PriorityRow(int Index, string GroupId, string Name, bool IsEnabled);

    private sealed record PluginRow(int Index, string Name, bool IsEnabled, string? Flags, string[] Masters, bool IsLocked, string? LockReason, string? Mod);

    [Verb("loadout priority list", "Lists the mods in priority order, lowest first. The last mod wins when two ship the same file.")]
    private static async Task<int> ListPriority([Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout to list")] Loadout.ReadOnly loadout,
        [Option("j", "json", "Print the order as JSON", true)] bool json,
        [Injected] ModPriorityOrderVariety variety)
    {
        var rows = PriorityRows(variety, loadout);
        if (json)
        {
            await renderer.TextLine(JsonSerializer.Serialize(rows, CliJson.Options));
            return 0;
        }

        foreach (var row in rows)
            await renderer.TextLine("{0}", $"{row.Index,4}  {row.Name}{(row.IsEnabled ? "" : "  (off)")}");
        await renderer.TextLine("{0}", WinnerLine(variety));
        return 0;
    }

    [Verb("loadout group priority get", "Shows where a group stands in the mod priority order")]
    private static async Task<int> GetGroupPriority([Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout the group is in")] Loadout.ReadOnly loadout,
        [Option("g", "group", "The group's name, or its id from 'loadout groups list --json' when two share a name")] string groupName,
        [Option("j", "json", "Print the row as JSON", true)] bool json,
        [Injected] ModPriorityOrderVariety variety)
    {
        var group = LoadoutGroupLookup.Find(loadout, groupName);
        var rows = PriorityRows(variety, loadout);
        var row = rows.FirstOrDefault(r => r.GroupId == group.Id.ToString())
                  ?? throw new LoadoutVerbRefusedException($"{MarkdownText.Code(group.AsLoadoutItem().Name)} is not in the priority order. A group is added to it when it is installed or its rules change.");

        if (json)
        {
            await renderer.TextLine(JsonSerializer.Serialize(row, CliJson.Options));
            return 0;
        }

        await renderer.TextLine("{0}", $"{row.Name} is at index {row.Index} of 0 to {rows.Length - 1}.");
        await renderer.TextLine("{0}", WinnerLine(variety));
        return 0;
    }

    [Verb("loadout group priority set", "Moves a group to an index in the mod priority order. 0 is the lowest priority and the last index wins; an index outside the list is clamped. A restore point is written first, see 'loadout revisions'.")]
    private static async Task<int> SetGroupPriority([Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout the group is in")] Loadout.ReadOnly loadout,
        [Option("g", "group", "The group's name, or its id from 'loadout groups list --json' when two share a name")] string groupName,
        [Option("i", "index", "The index to move the group to")] int index,
        [Injected] ModPriorityOrderVariety variety,
        [Injected] IConnection connection,
        [Injected] ILoggerFactory loggers,
        [Injected] CancellationToken token)
    {
        var group = LoadoutGroupLookup.Find(loadout, groupName);
        var name = group.AsLoadoutItem().Name;
        var groupId = LoadoutItemGroupId.From(group.Id);
        var sortOrder = variety.GetSortOrderIdFor(loadout.LoadoutId).Value;

        await variety.ReconcileSortOrder(sortOrder, token: token);
        var items = variety.GetSortOrderItems(sortOrder);
        var from = IndexOf(items, groupId);
        if (from < 0)
            throw new LoadoutVerbRefusedException($"{MarkdownText.Code(name)} is not in the priority order. A group is added to it when it is installed or its rules change.");

        var to = Math.Clamp(index, 0, items.Count - 1);
        var clamped = to != index ? $" (clamped from {index}; the list has indexes 0 to {items.Count - 1})" : "";
        if (to == from)
        {
            await renderer.TextLine("{0}", $"{name} is already at index {to}{clamped}.");
            return 0;
        }

        await LoadoutSnapshot.WriteRestorePoint(connection, loadout.LoadoutId);
        await MoveOrRefuse(variety, sortOrder, items[from].Key, items[to].Key, to > from, token);

        var landed = IndexOf(variety.GetSortOrderItems(sortOrder), groupId);
        loggers.CreateLogger(LogCategory).LogInformation("Moved group {Group} of loadout {Loadout} from priority index {From} to {To}", groupId, loadout.LoadoutId, from, landed);
        await renderer.TextLine("{0}", $"{name} moved from index {from} to {landed}{clamped}. 'loadout revisions' lists the restore point; 'loadout revert' undoes it.");
        return landed == to ? 0 : 1;
    }

    [Verb("loadout plugins list", "Lists the plugins in load order with their switch, masters and lock. The last plugin wins when two change the same record.")]
    private static async Task<int> ListPlugins([Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout to list")] Loadout.ReadOnly loadout,
        [Option("variety", "variety", "Which order to list when the game keeps more than one; its name as the Load Order page shows it", true)] string? varietyName,
        [Option("j", "json", "Print the plugins as JSON", true)] bool json,
        [Injected] CancellationToken token)
    {
        var variety = PluginVariety(loadout, varietyName);
        var sortOrder = await SortOrderFor(variety, loadout, token);
        var rows = PluginRows(variety, sortOrder);
        if (json)
        {
            await renderer.TextLine(JsonSerializer.Serialize(rows, CliJson.Options));
            return 0;
        }

        foreach (var row in rows)
        {
            var notes = new List<string>();
            if (row.Flags is not null) notes.Add(row.Flags);
            if (row.IsLocked) notes.Add($"locked: {row.LockReason}");
            else if (row.LockReason is not null) notes.Add($"switch locked: {row.LockReason}");
            if (row.Masters.Length > 0) notes.Add($"masters: {string.Join(", ", row.Masters)}");
            await renderer.TextLine("{0}", $"{row.Index,4}  {(row.IsEnabled ? "on " : "off")}  {row.Name}{(notes.Count > 0 ? $"  ({string.Join("; ", notes)})" : "")}");
        }
        await renderer.TextLine("{0}", WinnerLine(variety));
        return 0;
    }

    [Verb("loadout plugins set", "Puts the named plugins in the order given and leaves the others where they are: the first name keeps its place and each following one is moved just after the one before it. Refused, with nothing written, when a name is unknown or locked. A restore point is written first, see 'loadout revisions'.")]
    private static async Task<int> SetPluginOrder([Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout to reorder")] Loadout.ReadOnly loadout,
        [Option("o", "order", "Plugin file names separated by ';', in the order they should load, for example 'a.esp;b.esp'")] string order,
        [Option("variety", "variety", "Which order to change when the game keeps more than one; its name as the Load Order page shows it", true)] string? varietyName,
        [Injected] IConnection connection,
        [Injected] CancellationToken token)
    {
        var wanted = order.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (wanted.Length < 2)
            throw new LoadoutVerbRefusedException("Name at least two plugins, separated by ';', to say which loads after which.");
        if (wanted.Distinct(StringComparer.OrdinalIgnoreCase).Count() != wanted.Length)
            throw new LoadoutVerbRefusedException("A plugin is named twice in the order.");

        var variety = PluginVariety(loadout, varietyName);
        var sortOrder = await SortOrderFor(variety, loadout, token);
        await variety.ReconcileSortOrder(sortOrder, token: token);
        var items = variety.GetSortOrderItems(sortOrder);

        var byName = items.ToDictionary(item => item.DisplayName, StringComparer.OrdinalIgnoreCase);
        var unknown = wanted.Where(name => !byName.ContainsKey(name)).ToArray();
        if (unknown.Length > 0)
            throw new LoadoutVerbRefusedException($"The loadout has no plugin called {string.Join(", ", unknown.Select(n => $"{MarkdownText.Code(n)}"))}. 'loadout plugins list' shows the plugins.");
        var locked = wanted.Where(name => byName[name] is ISortItemDetails { IsLocked: true }).ToArray();
        if (locked.Length > 0)
            throw new LoadoutVerbRefusedException($"{string.Join(", ", locked.Select(MarkdownText.Code))} can't be moved, because the game always loads {(locked.Length == 1 ? "it" : "them")} first.");

        await LoadoutSnapshot.WriteRestorePoint(connection, loadout.LoadoutId);
        for (var i = 1; i < wanted.Length; i++)
            await MoveOrRefuse(variety, sortOrder, byName[wanted[i]].Key, byName[wanted[i - 1]].Key, after: true, token);

        var after = variety.GetSortOrderItems(sortOrder);
        var misplaced = new List<string>();
        for (var i = 1; i < wanted.Length; i++)
        {
            var previous = IndexOfName(after, wanted[i - 1]);
            if (previous < 0 || IndexOfName(after, wanted[i]) < previous) misplaced.Add(wanted[i]);
        }

        foreach (var row in PluginRows(variety, sortOrder))
            await renderer.TextLine("{0}", $"{row.Index,4}  {row.Name}");
        if (misplaced.Count > 0)
        {
            await renderer.TextLine("{0}", $"The game's rules kept {string.Join(", ", misplaced)} from loading after the plugin named before {(misplaced.Count == 1 ? "it" : "them")}.");
            return 1;
        }

        await renderer.TextLine("{0}", "'loadout revisions' lists the restore point; 'loadout revert' undoes this.");
        return 0;
    }

    [Verb("loadout plugins sort", "Sorts the plugins with LOOT's masterlist, as the Sort with LOOT button does. Exits 1 when LOOT finds no valid order, and then leaves the order alone. A restore point is written first, even when LOOT finds no valid order; see 'loadout revisions'.")]
    private static async Task<int> SortPlugins([Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout to sort")] Loadout.ReadOnly loadout,
        [Option("variety", "variety", "Which order to sort when the game keeps more than one; its name as the Load Order page shows it", true)] string? varietyName,
        [Injected] IConnection connection,
        [Injected] ILoggerFactory loggers,
        [Injected] CancellationToken token)
    {
        var variety = PluginVariety(loadout, varietyName, requireLoot: true);
        var source = (ILootSortSource)variety;
        var sortOrder = await SortOrderFor(variety, loadout, token);
        await variety.ReconcileSortOrder(sortOrder, token: token);

        await LoadoutSnapshot.WriteRestorePoint(connection, loadout.LoadoutId);
        LootSortOutcome outcome;
        try
        {
            outcome = await source.SortAsync(sortOrder, token);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The same contract the Sort with LOOT button follows: bad metadata is the user's to fix, so its message is shown.
            loggers.CreateLogger(LogCategory).LogWarning(e, "Sorting loadout {Loadout} with LOOT failed", loadout.LoadoutId);
            await renderer.TextLine("{0}", $"Sorting with LOOT failed: {e.Message}");
            return 1;
        }

        await renderer.TextLine("{0}", outcome.Succeeded ? "Sorted with LOOT." : "LOOT found no valid order, so the plugins stay as they were.");
        if (outcome.CycleText is { } cycle) await renderer.TextLine("{0}", $"Cycle: {cycle}");
        await renderer.TextLine("{0}", $"Masterlist: {outcome.MasterlistRevision}");
        if (outcome.OverlapEdgesSkipped) await renderer.TextLine("{0}", "Record and asset overlap rules were not applied.");
        if (outcome.UnevaluatedConditions > 0) await renderer.TextLine("{0}", $"{outcome.UnevaluatedConditions} masterlist conditions could not be evaluated and were skipped.");
        if (outcome.Succeeded) await renderer.TextLine("{0}", "'loadout revisions' lists the restore point; 'loadout revert' undoes this.");
        return outcome.Succeeded ? 0 : 1;
    }

    private static PriorityRow[] PriorityRows(ModPriorityOrderVariety variety, Loadout.ReadOnly loadout)
    {
        var sortOrder = variety.GetSortOrderIdFor(loadout.LoadoutId).Value;
        return variety.GetSortOrderItems(sortOrder)
            .Select(item => new PriorityRow(item.SortIndex, item.ModGroupId.HasValue ? item.ModGroupId.Value.Value.ToString() : "", item.DisplayName, item.IsActive))
            .ToArray();
    }

    private static PluginRow[] PluginRows(ISortOrderVariety variety, SortOrderId sortOrder) => variety.GetSortOrderItems(sortOrder)
        .Select(item =>
        {
            var details = item as ISortItemDetails;
            return new PluginRow(
                item.SortIndex,
                item.DisplayName,
                details?.IsEnabled ?? item.IsActive,
                details?.Flags,
                details?.Masters.ToArray() ?? [],
                details?.IsLocked ?? false,
                details?.LockReason?.ToString(),
                item.ModName);
        })
        .ToArray();

    /// <summary>
    /// The order the game keeps beside the mod priority order: the only variety of the game, or the one named by
    /// <paramref name="name"/> when there are several.
    /// </summary>
    private static ISortOrderVariety PluginVariety(Loadout.ReadOnly loadout, string? name, bool requireLoot = false)
    {
        var varieties = loadout.InstallationInstance.GetGame().SortOrderManager.GetSortOrderVarieties()
            .Where(variety => variety is not ModPriorityOrderVariety)
            .ToArray();
        if (varieties.Length == 0) throw new LoadoutVerbRefusedException(NoPluginOrder);

        if (name is not null)
        {
            var named = varieties.Where(v => string.Equals(v.SortOrderUiMetadata.SortOrderName, name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (named.Length == 0)
                throw new LoadoutVerbRefusedException($"No order is called {MarkdownText.Code(name)}. This game has: {string.Join(", ", varieties.Select(v => $"{MarkdownText.Code(v.SortOrderUiMetadata.SortOrderName)}"))}.");
            varieties = named;
        }

        if (requireLoot)
        {
            varieties = varieties.Where(v => v is ILootSortSource).ToArray();
            if (varieties.Length == 0) throw new LoadoutVerbRefusedException("LOOT can't sort this game's load order.");
        }

        if (varieties.Length > 1)
            throw new LoadoutVerbRefusedException($"This game keeps more than one order ({string.Join(", ", varieties.Select(v => $"{MarkdownText.Code(v.SortOrderUiMetadata.SortOrderName)}"))}). Pass --variety to say which.");
        return varieties[0];
    }

    private static async Task<SortOrderId> SortOrderFor(ISortOrderVariety variety, Loadout.ReadOnly loadout, CancellationToken token)
    {
        var existing = variety.GetSortOrderIdFor(loadout.LoadoutId);
        if (existing.HasValue) return existing.Value;

        // A loadout the Load Order page never opened has no order yet; it starts from the plugins the loadout holds.
        var created = await variety.GetOrCreateSortOrderFor(loadout.LoadoutId, loadout.LoadoutId, token);
        await variety.ReconcileSortOrder(created, token: token);
        return created;
    }

    private static async Task MoveOrRefuse(ISortOrderVariety variety, SortOrderId sortOrder, ISortItemKey item, ISortItemKey target, bool after, CancellationToken token)
    {
        try
        {
            await variety.MoveItems(sortOrder, [item], target, after ? TargetRelativePosition.AfterTarget : TargetRelativePosition.BeforeTarget, token: token);
        }
        catch (InvalidOperationException e)
        {
            // A move a stored rule forbids says so; the page shows the same text.
            throw new LoadoutVerbRefusedException(e.Message);
        }
    }

    private static int IndexOf(IReadOnlyList<IReactiveSortItem> items, LoadoutItemGroupId group)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].ModGroupId.HasValue && items[i].ModGroupId.Value == group) return i;
        }
        return -1;
    }

    private static int IndexOfName(IReadOnlyList<IReactiveSortItem> items, string name)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (string.Equals(items[i].DisplayName, name, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    private static string WinnerLine(ISortOrderVariety variety) => variety.IndexOverrideBehavior == IndexOverrideBehavior.GreaterIndexWins
        ? $"{LoadOrderConvention.LastLoadedWins}: the highest index wins."
        : $"{LoadOrderConvention.LastLoadedWins} in the app's list; this order stores its winner first, so the lowest index wins.";
}
