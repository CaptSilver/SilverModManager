// Follows esplugin src/record_id.rs (ResolvedRecordId::from_form_id) and src/plugin.rs (hashed_masters, count_override_records, overlaps_with).
using System.IO.Hashing;
using System.Text;

namespace SilverModManager.Games.Bethesda.LoadOrder;

/// <summary>
/// The records of one plugin as ids that can be compared with another plugin's: a FormID is resolved against the
/// plugin's master list into the plugin that owns it and the record's index inside that plugin. A record owned by
/// one of the plugin's masters is an override.
/// </summary>
/// <remarks>
/// <see cref="Resolve"/> is for every game but Starfield: the low 24 bits are the index inside the owning plugin and
/// the high byte is the position in the master list, whether or not the plugin is light. Starfield numbers each
/// master by its scale, so it goes through <see cref="ResolveStarfield"/>.
/// </remarks>
public sealed class PluginRecordIds
{
    // Sorted by object index, then owner, so two plugins' ids can be intersected in one pass.
    private readonly ResolvedId[] _ids;
    private ulong[]? _keys;

    private PluginRecordIds(ResolvedId[] ids, int overrideCount)
    {
        _ids = ids;
        OverrideCount = overrideCount;
    }

    /// <summary>How many records the plugin has.</summary>
    public int Count => _ids.Length;

    /// <summary>How many of them belong to one of the plugin's masters.</summary>
    public int OverrideCount { get; }

    /// <param name="pluginName">The name the plugin is loaded under; a record the plugin adds is owned by it.</param>
    /// <param name="masters">The plugin's masters in header order; position in this list is the high byte of their FormIDs.</param>
    /// <param name="formIds">The raw FormIDs from <see cref="PluginRecordWalker"/>.</param>
    public static PluginRecordIds Resolve(string pluginName, IReadOnlyList<string> masters, IReadOnlyList<uint> formIds)
    {
        // A header past 256 masters has no byte to name the extras by, so they own nothing.
        var masterOwners = new ulong[Math.Min(masters.Count, 256)];
        for (var i = 0; i < masterOwners.Length; i++) masterOwners[i] = Owner(masters[i]);
        var self = Owner(pluginName);

        var ids = new ResolvedId[formIds.Count];
        var overrides = 0;
        for (var i = 0; i < ids.Length; i++)
        {
            var formId = formIds[i];
            var index = formId >> 24;
            var isOverride = index < masterOwners.Length;
            if (isOverride) overrides++;

            ids[i] = new ResolvedId(formId & 0x00FF_FFFF, isOverride ? masterOwners[index] : self);
        }

        Array.Sort(ids);
        return new PluginRecordIds(ids, overrides);
    }

    /// <summary>
    /// Resolves a Starfield plugin's FormIDs. Each master gets a mod index from its own scale: full masters count up
    /// from 0x00, medium ones from 0xFD000000 in steps of 0x10000 (16 bits each) and small ones from 0xFE000000 in steps of 0x1000 (12 bits each), in
    /// header order within a scale. A FormID that names no master is the plugin's own, and its object index keeps as
    /// many bits as the plugin's own scale gives it, whatever mod index it was written with.
    /// </summary>
    /// <param name="scale">The plugin's own scale.</param>
    /// <param name="masters">The plugin's masters in header order, each with its own scale.</param>
    public static PluginRecordIds ResolveStarfield(
        string pluginName, PluginScale scale, IReadOnlyList<(string Name, PluginScale Scale)> masters, IReadOnlyList<uint> formIds)
    {
        // A master is found by the bits of a FormID above its object index, so each scale gets a table keyed by those
        // bits. When two masters could match (more than 253 full ones), the earlier in the header wins.
        var full = new Dictionary<uint, (int Position, ulong Owner)>();
        var medium = new Dictionary<uint, (int Position, ulong Owner)>();
        var small = new Dictionary<uint, (int Position, ulong Owner)>();
        uint fullMask = 0, mediumMask = 0xFD00_0000, smallMask = 0xFE00_0000;
        for (var position = 0; position < masters.Count; position++)
        {
            var (name, masterScale) = masters[position];
            var owner = Owner(name);
            // Masks wrap like the game's 32-bit arithmetic does for a header with too many masters.
            unchecked
            {
                switch (masterScale)
                {
                    case PluginScale.Full:
                        full.TryAdd(fullMask >> 24, (position, owner));
                        fullMask += 0x0100_0000;
                        break;
                    case PluginScale.Medium:
                        medium.TryAdd(mediumMask >> 16, (position, owner));
                        mediumMask += 0x0001_0000;
                        break;
                    default:
                        small.TryAdd(smallMask >> 12, (position, owner));
                        smallMask += 0x0000_1000;
                        break;
                }
            }
        }

        var self = Owner(pluginName);
        var ownMask = scale switch
        {
            PluginScale.Full => 0x00FF_FFFFu,
            PluginScale.Medium => 0x0000_FFFFu,
            _ => 0x0000_0FFFu,
        };

        var ids = new ResolvedId[formIds.Count];
        var overrides = 0;
        for (var i = 0; i < ids.Length; i++)
        {
            var formId = formIds[i];
            int best = int.MaxValue;
            var bestOwner = 0UL;
            var bestMask = 0u;

            if (full.TryGetValue(formId >> 24, out var f) && f.Position < best) (best, bestOwner, bestMask) = (f.Position, f.Owner, 0x00FF_FFFFu);
            if (medium.TryGetValue(formId >> 16, out var m) && m.Position < best) (best, bestOwner, bestMask) = (m.Position, m.Owner, 0x0000_FFFFu);
            if (small.TryGetValue(formId >> 12, out var s) && s.Position < best) (best, bestOwner, bestMask) = (s.Position, s.Owner, 0x0000_0FFFu);

            if (best != int.MaxValue)
            {
                overrides++;
                ids[i] = new ResolvedId(formId & bestMask, bestOwner);
            }
            else
            {
                ids[i] = new ResolvedId(formId & ownMask, self);
            }
        }

        Array.Sort(ids);
        return new PluginRecordIds(ids, overrides);
    }

    /// <summary>
    /// Resolves a Morrowind or OpenMW plugin's record ids. A record is an override when one of the masters has a
    /// record with the same id; the ids carry no mod index to say which.
    /// </summary>
    /// <param name="ids">The plugin's ids from <see cref="PluginRecordWalker.ReadNamedIds"/>, ascending.</param>
    /// <param name="masterIds">Each master's ids from the same call, ascending.</param>
    public static PluginRecordIds ResolveNamed(IReadOnlyList<ulong> ids, IReadOnlyList<ulong[]> masterIds)
    {
        var resolved = new ResolvedId[ids.Count];
        var overrides = 0;
        for (var i = 0; i < resolved.Length; i++)
        {
            resolved[i] = new ResolvedId(0, ids[i]);
            if (masterIds.Any(master => Array.BinarySearch(master, ids[i]) >= 0)) overrides++;
        }

        Array.Sort(resolved);
        return new PluginRecordIds(resolved, overrides);
    }

    /// <summary>
    /// One 64-bit key per record, equal for two records exactly when <see cref="Overlaps"/> counts them as the
    /// same one, bar a hash collision that would only cost a wasted comparison.
    /// </summary>
    public IReadOnlyList<ulong> Keys => _keys ??= [.. _ids.Select(id => id.Owner ^ (id.ObjectIndex * 0x9E37_79B9_7F4A_7C15UL))];

    /// <summary>True when the two plugins have a record with the same owner and index, as an override or as the original.</summary>
    public bool Overlaps(PluginRecordIds other)
    {
        var left = 0;
        var right = 0;
        while (left < _ids.Length && right < other._ids.Length)
        {
            var order = _ids[left].CompareTo(other._ids[right]);
            if (order == 0) return true;

            if (order < 0) left++;
            else right++;
        }

        return false;
    }

    // Plugin names compare without case, as they do in the load order.
    private static ulong Owner(string pluginName) => XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(pluginName.ToLowerInvariant()));

    private readonly record struct ResolvedId(uint ObjectIndex, ulong Owner) : IComparable<ResolvedId>
    {
        public int CompareTo(ResolvedId other)
        {
            var byIndex = ObjectIndex.CompareTo(other.ObjectIndex);
            return byIndex != 0 ? byIndex : Owner.CompareTo(other.Owner);
        }
    }
}
