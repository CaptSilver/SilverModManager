using NexusMods.Games.Bethesda.Loot.Metadata;
using NexusMods.Games.Bethesda.Loot.Sorting;

namespace NexusMods.Games.Bethesda.Loot.Database;

// Follows libloot's database/mod.rs (Database) and database/conditions.rs.

/// <summary>Whether lookups include the userlist's metadata.</summary>
public enum MergeMode
{
    WithUserMetadata,
    WithoutUserMetadata,
}

/// <summary>Whether lookups drop metadata whose condition does not hold.</summary>
public enum EvalMode
{
    DoNotEvaluate,

    /// <summary>
    /// Drops every entry that has a condition. There is no condition evaluator yet, so an entry
    /// that LOOT might have kept is treated as not applying, and each one dropped is counted in the
    /// lookup's result. Dropping a conditional load-after can only lose an edge, where keeping it
    /// could add a cycle LOOT would not see.
    /// </summary>
    Evaluate,
}

/// <summary>
/// The loaded masterlist and userlist, and lookups that merge them the way LOOT does.
/// </summary>
/// <remarks>
/// Not thread-safe: load, then read from one thread, or lock around both.
/// </remarks>
public sealed class LootDatabase
{
    private MetadataDocument _masterlist = MetadataDocument.Empty;
    private MetadataDocument _userlist = MetadataDocument.Empty;

    /// <summary>
    /// Loads the masterlist, replacing any masterlist loaded before. If the text is invalid the
    /// previous masterlist stays.
    /// </summary>
    /// <exception cref="MetadataLoadException"/>
    /// <exception cref="MetadataParseException"/>
    /// <exception cref="YamlMergeKeyException"/>
    public void LoadMasterlist(string yaml) => _masterlist = MetadataDocument.Load(yaml);

    /// <summary>
    /// Loads the masterlist with its <c>prelude:</c> block replaced by <paramref name="prelude"/>,
    /// replacing any masterlist loaded before.
    /// </summary>
    /// <exception cref="MetadataLoadException"/>
    /// <exception cref="MetadataParseException"/>
    /// <exception cref="YamlMergeKeyException"/>
    public void LoadMasterlistWithPrelude(string yaml, string prelude) =>
        _masterlist = MetadataDocument.LoadWithPrelude(yaml, prelude);

    /// <summary>
    /// Loads the userlist, replacing any userlist loaded before. If the text is invalid the
    /// previous userlist stays.
    /// </summary>
    /// <exception cref="MetadataLoadException"/>
    /// <exception cref="MetadataParseException"/>
    /// <exception cref="YamlMergeKeyException"/>
    public void LoadUserlist(string yaml) => _userlist = MetadataDocument.Load(yaml);

    /// <summary>
    /// The metadata for a plugin. With user metadata it is the userlist's entry with the
    /// masterlist's merged in, so user values come first in each list and win for the group.
    /// </summary>
    /// <returns>The metadata, or null if nothing but the name is set, and the number of entries
    /// dropped because their condition could not be evaluated.</returns>
    /// <exception cref="InvalidPluginNameException">The plugin name contains regex characters and is not a valid regex.</exception>
    public (PluginMetadata? Metadata, int UnevaluatedConditions) PluginMetadata(
        string pluginName, MergeMode mergeMode, EvalMode evalMode)
    {
        var metadata = _masterlist.FindPlugin(pluginName);

        if (mergeMode == MergeMode.WithUserMetadata && _userlist.FindPlugin(pluginName) is { } user)
        {
            metadata = metadata is null ? user : user.MergeMetadata(metadata);
        }

        return Evaluate(metadata, evalMode);
    }

    /// <summary>The metadata the userlist alone holds for a plugin.</summary>
    /// <exception cref="InvalidPluginNameException">The plugin name contains regex characters and is not a valid regex.</exception>
    public (PluginMetadata? Metadata, int UnevaluatedConditions) PluginUserMetadata(string pluginName, EvalMode evalMode) =>
        Evaluate(_userlist.FindPlugin(pluginName), evalMode);

    /// <summary>The messages that apply to the whole load order, the masterlist's first.</summary>
    public (IReadOnlyList<Message> Messages, int UnevaluatedConditions) GeneralMessages(MergeMode mergeMode, EvalMode evalMode)
    {
        IEnumerable<Message> messages = _masterlist.Globals;
        if (mergeMode == MergeMode.WithUserMetadata) messages = messages.Concat(_userlist.Globals);

        return FilterMessages(messages, evalMode);
    }

    /// <summary>The messages that apply to the whole load order that the userlist holds.</summary>
    public (IReadOnlyList<Message> Messages, int UnevaluatedConditions) UserGeneralMessages(EvalMode evalMode) =>
        FilterMessages(_userlist.Globals, evalMode);

    /// <summary>
    /// The Bash Tags the lists name. Tag suggestions on plugins can use others. The userlist's tags
    /// follow the masterlist's without removing duplicates.
    /// </summary>
    public IReadOnlyList<string> KnownBashTags(MergeMode mergeMode) =>
        mergeMode == MergeMode.WithUserMetadata ? [.._masterlist.BashTags, .._userlist.BashTags] : [.._masterlist.BashTags];

    /// <summary>
    /// The groups the lists define. With user metadata a userlist group of the same name overrides
    /// the description and gains the extra after-groups, and new userlist groups follow.
    /// </summary>
    public IReadOnlyList<Group> Groups(MergeMode mergeMode) =>
        mergeMode == MergeMode.WithUserMetadata ? MergeGroups(_masterlist.Groups, _userlist.Groups) : [.._masterlist.Groups];

    /// <summary>The groups the userlist defines or extends.</summary>
    public IReadOnlyList<Group> UserGroups => _userlist.Groups;

    /// <summary>
    /// The path between two groups through their load-after edges, preferring user-defined edges over
    /// masterlist ones. Not necessarily the path through the fewest groups. Empty if there is none.
    /// </summary>
    /// <exception cref="BuildGroupsGraphException">The groups do not form a valid graph.</exception>
    /// <exception cref="GroupsPathException">A group is not defined, or the user edges contain a cycle.</exception>
    public IReadOnlyList<Vertex> GroupsPath(string fromGroupName, string toGroupName) =>
        GroupsGraph.Build(_masterlist.Groups, _userlist.Groups).FindPath(fromGroupName, toGroupName);

    private static (PluginMetadata? Metadata, int UnevaluatedConditions) Evaluate(PluginMetadata? metadata, EvalMode evalMode)
    {
        if (metadata is null || evalMode == EvalMode.DoNotEvaluate) return (metadata, 0);

        var dropped = 0;
        EquatableArray<T> KeepUnconditioned<T>(EquatableArray<T> items, Func<T, string?> condition)
        {
            var kept = items.Where(item => condition(item) is null).ToArray();
            dropped += items.Count - kept.Length;
            return kept;
        }

        EquatableArray<T> DropAll<T>(EquatableArray<T> items)
        {
            dropped += items.Count;
            return [];
        }

        // LOOT wraps every cleaning entry in checksum(<plugin>, <crc>) and keeps it only when the plugin's
        // CRC matches. That needs the plugin's bytes, so none can be kept, with or without a condition of their own.
        var evaluated = metadata with
        {
            LoadAfter = KeepUnconditioned(metadata.LoadAfter, f => f.Condition),
            Requirements = KeepUnconditioned(metadata.Requirements, f => f.Condition),
            Incompatibilities = KeepUnconditioned(metadata.Incompatibilities, f => f.Condition),
            Messages = KeepUnconditioned(metadata.Messages, m => m.Condition),
            Tags = KeepUnconditioned(metadata.Tags, t => t.Condition),
            DirtyInfo = DropAll(metadata.DirtyInfo),
            CleanInfo = DropAll(metadata.CleanInfo),
        };

        return (evaluated.HasNameOnly ? null : evaluated, dropped);
    }

    private static (IReadOnlyList<Message> Messages, int UnevaluatedConditions) FilterMessages(IEnumerable<Message> messages, EvalMode evalMode)
    {
        var all = messages.ToList();
        if (evalMode == EvalMode.DoNotEvaluate) return (all, 0);

        var kept = all.Where(m => m.Condition is null).ToList();
        return (kept, all.Count - kept.Count);
    }

    private static List<Group> MergeGroups(IReadOnlyList<Group> masterlist, IReadOnlyList<Group> userlist)
    {
        var groups = masterlist.ToList();
        List<Group> added = [];

        foreach (var user in userlist)
        {
            var index = groups.FindIndex(g => g.Name == user.Name);
            if (index < 0)
            {
                added.Add(user);
                continue;
            }

            var group = groups[index];
            if (user.Description is not null) group = group with { Description = user.Description };
            if (user.AfterGroups.Count > 0) group = group with { AfterGroups = [..group.AfterGroups, ..user.AfterGroups] };
            groups[index] = group;
        }

        groups.AddRange(added);
        return groups;
    }
}
