// Ported from libloadorder src/load_order/mutable.rs (validate_load_order and the checks it calls).
namespace NexusMods.Games.Bethesda.LoadOrder.Ordering;

public enum PluginOrderErrorKind
{
    InvalidEarlyLoadingPluginPosition,
    NonMasterBeforeMaster,
    InvalidBlueprintPluginPosition,
    UnrepresentedHoist,
}

/// <summary>
/// What <see cref="PluginOrderValidation"/> found wrong. Which fields are meaningful depends on the kind:
/// early-loader and blueprint errors carry <see cref="Position"/> and <see cref="ExpectedPosition"/>;
/// <see cref="Other"/> is the non-master for <c>NonMasterBeforeMaster</c> (with <see cref="Plugin"/> the master
/// file reached while a non-master that is not one of its masters still sits before it) and the master file for <c>UnrepresentedHoist</c> (with
/// <see cref="Plugin"/> the master it loads ahead of).
/// </summary>
public sealed record PluginOrderValidationError(
    PluginOrderErrorKind Kind,
    string Plugin,
    string? Other = null,
    int Position = -1,
    int ExpectedPosition = -1);

/// <summary>
/// The rules libloadorder enforces on a load order before it saves one, which is also what the games
/// do with the file they are given. One rule is looser: an early loader may name a master that loads after
/// it, where libloadorder would ask for a hoist that its own early-loader rule then rejects.
/// </summary>
public static class PluginOrderValidation
{
    /// <returns>The first rule the order breaks, or null when it is valid.</returns>
    /// <exception cref="ArgumentException">An entry name appears twice, or the order names a plugin with no entry.</exception>
    public static PluginOrderValidationError? Validate(
        IReadOnlyList<string> order,
        IReadOnlyList<PluginOrderEntry> entries,
        IReadOnlyList<string> earlyLoaders)
    {
        var lookup = PluginOrderResolver.IndexEntries(entries);
        var plugins = new PluginOrderEntry[order.Count];
        for (var i = 0; i < order.Count; i++)
        {
            plugins[i] = lookup.TryGetValue(order[i], out var entry)
                ? entry
                : throw new ArgumentException($"The order names '{order[i]}', which has no entry.", nameof(order));
        }

        return EarlyLoaderPositions(plugins, earlyLoaders)
               ?? NoUnhoistedNonMastersBeforeMasters(plugins)
               ?? NoNonBlueprintPluginsAfterBlueprintPlugins(plugins)
               ?? PluginsLoadBeforeTheirMasters(plugins);
    }

    private static PluginOrderValidationError? EarlyLoaderPositions(PluginOrderEntry[] plugins, IReadOnlyList<string> earlyLoaders)
    {
        var missing = 0;
        for (var i = 0; i < earlyLoaders.Count; i++)
        {
            // A blueprint master never actually loads early, so it counts as missing.
            var pos = Array.FindIndex(plugins, p => !p.IsBlueprintMaster && PluginNameComparer.Instance.Equals(p.Name, earlyLoaders[i]));
            if (pos < 0)
            {
                missing++;
                continue;
            }

            var expected = i - missing;
            if (pos != expected)
                return new PluginOrderValidationError(PluginOrderErrorKind.InvalidEarlyLoadingPluginPosition, earlyLoaders[i], null, pos, expected);
        }

        return null;
    }

    private static PluginOrderValidationError? NoUnhoistedNonMastersBeforeMasters(PluginOrderEntry[] plugins)
    {
        var firstNonMaster = Array.FindIndex(plugins, p => !p.IsMaster);
        if (firstNonMaster < 0) firstNonMaster = plugins.Length;

        // Blueprint masters load after the non-masters, so they do not count here.
        var lastMaster = Array.FindLastIndex(plugins, p => p.IsMaster && !p.IsBlueprintMaster);
        if (lastMaster < 0 || firstNonMaster >= lastMaster) return null;

        // Non-masters seen so far, in order. A master file removes its own masters; anything still here
        // when it is reached was not hoisted ahead of it.
        var pending = new List<string>();
        for (var i = firstNonMaster; i <= lastMaster; i++)
        {
            var plugin = plugins[i];
            if (!plugin.IsMaster)
            {
                pending.Add(plugin.Name);
                continue;
            }

            foreach (var master in plugin.Masters)
                pending.RemoveAll(n => PluginNameComparer.Instance.Equals(n, master));

            if (pending.Count > 0)
                return new PluginOrderValidationError(PluginOrderErrorKind.NonMasterBeforeMaster, plugin.Name, pending[0]);
        }

        return null;
    }

    private static PluginOrderValidationError? NoNonBlueprintPluginsAfterBlueprintPlugins(PluginOrderEntry[] plugins)
    {
        var firstBlueprint = Array.FindIndex(plugins, p => p.IsBlueprintMaster);
        if (firstBlueprint < 0) return null;

        var lastNonBlueprint = Array.FindLastIndex(plugins, p => !p.IsBlueprintMaster);
        return lastNonBlueprint > firstBlueprint
            ? new PluginOrderValidationError(PluginOrderErrorKind.InvalidBlueprintPluginPosition, plugins[firstBlueprint].Name, null, firstBlueprint, lastNonBlueprint)
            : null;
    }

    private static PluginOrderValidationError? PluginsLoadBeforeTheirMasters(PluginOrderEntry[] plugins)
    {
        var later = new Dictionary<string, PluginOrderEntry>(PluginNameComparer.Instance);
        for (var i = plugins.Length - 1; i >= 0; i--)
        {
            var plugin = plugins[i];
            // An early loader is placed by the game, not by its masters, so it can name one that loads after it.
            if (plugin.IsMaster && !plugin.LoadsEarly)
            {
                foreach (var master in plugin.Masters)
                {
                    if (!later.TryGetValue(master, out var found)) continue;

                    // A non-blueprint plugin may depend on a blueprint plugin that loads after it.
                    if (plugin.IsBlueprintMaster || !found.IsBlueprintMaster)
                        return new PluginOrderValidationError(PluginOrderErrorKind.UnrepresentedHoist, found.Name, plugin.Name);
                    break;
                }
            }

            later[plugin.Name] = plugin;
        }

        return null;
    }
}
