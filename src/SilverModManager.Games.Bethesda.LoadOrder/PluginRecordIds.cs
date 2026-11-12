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
/// Every game but Starfield uses the low 24 bits as the index inside the owning plugin and the high byte as the
/// position in the master list, whether or not the plugin is light. Starfield's medium and small plugins resolve
/// differently and are not covered here.
/// </remarks>
public sealed class PluginRecordIds
{
    // Sorted by object index, then owner, so two plugins' ids can be intersected in one pass.
    private readonly ResolvedId[] _ids;

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
