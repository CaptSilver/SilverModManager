using SilverModManager.Games.Bethesda.Loot.Conditions;
using SilverModManager.Games.Bethesda.Loot.Metadata;
using SilverModManager.Games.Bethesda.Loot.Sorting;

namespace SilverModManager.Games.Bethesda.Loot.Database;

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
    /// Keeps the entries whose condition holds. An entry whose condition is false is dropped, and
    /// one that cannot be evaluated is dropped and counted in the lookup's result. Without a condition
    /// evaluator every entry that has a condition is in the second group: dropping a conditional
    /// load-after can only lose an edge, where keeping it could add a cycle LOOT would not see.
    /// </summary>
    Evaluate,
}

/// <summary>
/// The loaded masterlist and userlist, and lookups that merge them the way LOOT does.
/// </summary>
/// <param name="evaluator">Decides conditions. Without one, <see cref="EvalMode.Evaluate"/> treats every condition as unevaluable.</param>
/// <remarks>
/// Not thread-safe: load, then read from one thread, or lock around both.
/// </remarks>
public sealed class LootDatabase(IConditionEvaluator? evaluator = null)
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

        return EvaluateMetadata(pluginName, metadata, evalMode);
    }

    /// <summary>The metadata the userlist alone holds for a plugin.</summary>
    /// <exception cref="InvalidPluginNameException">The plugin name contains regex characters and is not a valid regex.</exception>
    public (PluginMetadata? Metadata, int UnevaluatedConditions) PluginUserMetadata(string pluginName, EvalMode evalMode) =>
        EvaluateMetadata(pluginName, _userlist.FindPlugin(pluginName), evalMode);

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

    /// <summary>
    /// Whether <paramref name="condition"/> holds, or null when it cannot be told: there is no evaluator, the
    /// text is not a condition, or a file it needs could not be read.
    /// </summary>
    public bool? Evaluate(string condition)
    {
        if (evaluator is null) return null;

        try
        {
            return evaluator.Evaluate(condition);
        }
        catch (Exception e) when (e is ConditionEvaluationException or ConditionParseException)
        {
            return null;
        }
    }

    private (PluginMetadata? Metadata, int UnevaluatedConditions) EvaluateMetadata(
        string pluginName, PluginMetadata? metadata, EvalMode evalMode)
    {
        if (metadata is null || evalMode == EvalMode.DoNotEvaluate) return (metadata, 0);

        var dropped = 0;
        bool Holds(string? condition)
        {
            if (condition is null) return true;
            if (Evaluate(condition) is { } holds) return holds;

            dropped++;
            return false;
        }

        EquatableArray<T> Keep<T>(EquatableArray<T> items, Func<T, string?> condition) =>
            items.Where(item => Holds(condition(item))).ToArray();

        // LOOT wraps every cleaning entry in checksum(<plugin>, <crc>) and keeps it only when the plugin's
        // CRC matches, and the entry's own condition has to hold as well.
        EquatableArray<PluginCleaningData> KeepCleaning(EquatableArray<PluginCleaningData> items) =>
            items.Where(item => Holds(CleaningCondition(pluginName, item))).ToArray();

        var evaluated = metadata with
        {
            LoadAfter = Keep(metadata.LoadAfter, f => f.Condition),
            Requirements = Keep(metadata.Requirements, f => f.Condition),
            Incompatibilities = Keep(metadata.Incompatibilities, f => f.Condition),
            Messages = Keep(metadata.Messages, m => m.Condition),
            Tags = Keep(metadata.Tags, t => t.Condition),
            DirtyInfo = KeepCleaning(metadata.DirtyInfo),
            CleanInfo = KeepCleaning(metadata.CleanInfo),
        };

        return (evaluated.HasNameOnly ? null : evaluated, dropped);
    }

    private static string CleaningCondition(string pluginName, PluginCleaningData data)
    {
        var checksum = $"checksum(\"{pluginName}\", {data.Crc:X8})";
        return data.Condition is null ? checksum : $"{checksum} and ({data.Condition})";
    }

    private (IReadOnlyList<Message> Messages, int UnevaluatedConditions) FilterMessages(IEnumerable<Message> messages, EvalMode evalMode)
    {
        var all = messages.ToList();
        if (evalMode == EvalMode.DoNotEvaluate) return (all, 0);

        var kept = new List<Message>();
        var dropped = 0;
        foreach (var message in all)
        {
            if (message.Condition is null) kept.Add(message);
            else if (Evaluate(message.Condition) is { } holds)
            {
                if (holds) kept.Add(message);
            }
            else dropped++;
        }

        return (kept, dropped);
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
