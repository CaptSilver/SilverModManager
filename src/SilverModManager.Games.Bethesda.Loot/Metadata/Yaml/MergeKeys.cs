using SilverModManager.Games.Bethesda.Loot.Metadata;
using YamlDotNet.RepresentationModel;

namespace SilverModManager.Games.Bethesda.Loot.Metadata.Yaml;

/// <summary>
/// Resolves YAML <c>&lt;&lt;</c> merge keys. libloot does this itself instead of leaving it to
/// its YAML library, and so does this. Follows libloot's metadata/yaml/merge.rs.
/// </summary>
internal static class MergeKeys
{
    private const string MergeKey = "<<";

    /// <summary>
    /// Merges every <c>&lt;&lt;</c> key in <paramref name="root"/> into its mapping, in place. A
    /// mapping's own key wins over a merged one, and so does an earlier mapping in a merged sequence.
    /// </summary>
    /// <remarks>
    /// An alias is the same node as its anchor here, so each node is processed once however often
    /// it is reached; that also stops a self-referencing alias from recursing forever.
    /// </remarks>
    /// <exception cref="YamlMergeKeyException">A merge key's value is not a mapping or a sequence of mappings.</exception>
    public static YamlNode Process(YamlNode root)
    {
        Visit(root, new HashSet<YamlNode>(ReferenceEqualityComparer.Instance));
        return root;
    }

    private static void Visit(YamlNode node, HashSet<YamlNode> seen)
    {
        switch (node)
        {
            case YamlSequenceNode sequence when seen.Add(sequence):
                foreach (var child in sequence.Children) Visit(child, seen);
                break;
            case YamlMappingNode mapping when seen.Add(mapping):
                foreach (var value in mapping.Children.Values.ToList()) Visit(value, seen);
                Merge(mapping);
                break;
        }
    }

    private static void Merge(YamlMappingNode mapping)
    {
        var key = mapping.Children.Keys.FirstOrDefault(k => k is YamlScalarNode { Value: MergeKey });
        if (key is null) return;

        var value = mapping.Children[key];
        mapping.Children.Remove(key);

        switch (value)
        {
            case YamlSequenceNode sequence:
                foreach (var element in sequence.Children)
                {
                    if (element is not YamlMappingNode source) throw Invalid(element);
                    MergeInto(mapping, source);
                }

                break;
            case YamlMappingNode source:
                MergeInto(mapping, source);
                break;
            default:
                throw Invalid(value);
        }
    }

    private static void MergeInto(YamlMappingNode target, YamlMappingNode source)
    {
        // Copied first: a mapping can be merged into itself through an alias.
        foreach (var (key, value) in source.Children.ToList())
        {
            if (!target.Children.ContainsKey(key)) target.Children.Add(key, value);
        }
    }

    private static YamlMergeKeyException Invalid(YamlNode value) =>
        new(SourcePosition.From(value.Start), YamlText.Describe(value));
}
