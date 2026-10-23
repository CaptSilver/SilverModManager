namespace SilverModManager.Games.Bethesda.Loot.Metadata;

/// <summary>
/// Everything LOOT knows about one plugin, or about every plugin matching a regex name. Follows
/// libloot's metadata/plugin_metadata.rs.
/// </summary>
public sealed record PluginMetadata
{
    private readonly PluginName _name;

    /// <exception cref="InvalidPluginNameException">The name is a regex that does not compile.</exception>
    public PluginMetadata(string name) => _name = new PluginName(name);

    /// <summary>The plugin filename, or its regex, without a trailing <c>.ghost</c>.</summary>
    public string Name => _name.Value;

    /// <summary>The plugin's group, or null for the default group.</summary>
    public string? Group { get; init; }

    public EquatableArray<File> LoadAfter { get; init; }

    public EquatableArray<File> Requirements { get; init; }

    public EquatableArray<File> Incompatibilities { get; init; }

    public EquatableArray<Message> Messages { get; init; }

    public EquatableArray<Tag> Tags { get; init; }

    public EquatableArray<PluginCleaningData> DirtyInfo { get; init; }

    public EquatableArray<PluginCleaningData> CleanInfo { get; init; }

    public EquatableArray<Location> Locations { get; init; }

    /// <summary>True if the name contains any of <c>:\*?|</c>.</summary>
    public bool IsRegexPlugin => _name.IsRegex;

    /// <summary>
    /// True if nothing but the name is set. Merging such metadata changes nothing.
    /// </summary>
    public bool HasNameOnly =>
        Group is null
        && LoadAfter.Count == 0
        && Requirements.Count == 0
        && Incompatibilities.Count == 0
        && Messages.Count == 0
        && Tags.Count == 0
        && DirtyInfo.Count == 0
        && CleanInfo.Count == 0
        && Locations.Count == 0;

    /// <summary>
    /// Whether <paramref name="otherName"/>, which must be a literal filename, is matched by this
    /// metadata's name: case-insensitively for a plain name, as a regex for a regex name.
    /// </summary>
    public bool NameMatches(string otherName) => _name.Matches(otherName);

    /// <summary>
    /// This metadata with <paramref name="other"/>'s merged in. The name stays, this group wins when
    /// set, and each list gains the elements of <paramref name="other"/> it does not already hold,
    /// except messages, which are always appended.
    /// </summary>
    public PluginMetadata MergeMetadata(PluginMetadata other)
    {
        if (other.HasNameOnly) return this;

        return this with
        {
            Group = Group ?? other.Group,
            LoadAfter = MergeSlices(LoadAfter, other.LoadAfter),
            Requirements = MergeSlices(Requirements, other.Requirements),
            Incompatibilities = MergeSlices(Incompatibilities, other.Incompatibilities),
            Tags = MergeSlices(Tags, other.Tags),
            Messages = [..Messages, ..other.Messages],
            DirtyInfo = MergeSlices(DirtyInfo, other.DirtyInfo),
            CleanInfo = MergeSlices(CleanInfo, other.CleanInfo),
            Locations = MergeSlices(Locations, other.Locations),
        };
    }

    /// <summary>
    /// Drops the load-after and requirement files whose constraint does not hold. Files without a
    /// constraint stay.
    /// </summary>
    /// <param name="evaluate">Evaluates a constraint condition string.</param>
    public PluginMetadata FilterByConstraints(Func<string, bool> evaluate)
    {
        return this with
        {
            LoadAfter = FilterFiles(LoadAfter, evaluate),
            Requirements = FilterFiles(Requirements, evaluate),
        };
    }

    private static EquatableArray<File> FilterFiles(EquatableArray<File> files, Func<string, bool> evaluate)
    {
        return files.Where(f => f.Constraint is null || evaluate(f.Constraint)).ToArray();
    }

    /// <summary>
    /// Each source element is tested against the original target, not the growing result, so
    /// duplicates inside the source are all appended (libloot's <c>merge_slices</c>).
    /// </summary>
    private static EquatableArray<T> MergeSlices<T>(EquatableArray<T> target, EquatableArray<T> source)
    {
        var merged = target.ToList();
        foreach (var element in source)
        {
            if (!target.Contains(element)) merged.Add(element);
        }

        return merged.ToArray();
    }
}
