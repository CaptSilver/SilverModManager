using NexusMods.Games.Bethesda.Loot.Metadata.Yaml;
using YamlDotNet.RepresentationModel;

namespace NexusMods.Games.Bethesda.Loot.Database;

/// <summary>
/// Removes every condition and constraint from a masterlist, so a sort can be compared with
/// libloot's without either side evaluating conditions.
/// </summary>
public static class MasterlistStripper
{
    private static readonly string[] ConditionKeys = ["condition", "constraint"];

    /// <summary>
    /// <paramref name="yaml"/> with every <c>condition</c> and <c>constraint</c> key removed from
    /// every mapping, at any depth, written back as YAML. The same text always gives the same
    /// result, and stripping stripped text changes nothing.
    /// </summary>
    /// <remarks>
    /// Merge keys are left alone, so the loader still resolves them. Aliases share their anchored
    /// node, so a condition removed from one is gone from all its uses.
    /// </remarks>
    /// <exception cref="NexusMods.Games.Bethesda.Loot.Metadata.MetadataParseException">The text is not valid YAML.</exception>
    public static string Strip(string yaml)
    {
        var stream = YamlLoader.Load(yaml);

        var seen = new HashSet<YamlNode>(ReferenceEqualityComparer.Instance);
        foreach (var document in stream.Documents) Strip(document.RootNode, seen);

        var writer = new StringWriter { NewLine = "\n" };
        stream.Save(writer, assignAnchors: false);
        return writer.ToString();
    }

    private static void Strip(YamlNode node, HashSet<YamlNode> seen)
    {
        switch (node)
        {
            case YamlSequenceNode sequence when seen.Add(sequence):
                foreach (var child in sequence.Children) Strip(child, seen);
                break;
            case YamlMappingNode mapping when seen.Add(mapping):
                foreach (var key in mapping.Children.Keys.Where(IsConditionKey).ToList()) mapping.Children.Remove(key);
                foreach (var value in mapping.Children.Values) Strip(value, seen);
                break;
        }
    }

    private static bool IsConditionKey(YamlNode key) =>
        key is YamlScalarNode { Value: { } value } && ConditionKeys.Contains(value);
}
