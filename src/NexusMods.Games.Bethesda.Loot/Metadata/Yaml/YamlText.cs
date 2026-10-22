using YamlDotNet.RepresentationModel;

namespace NexusMods.Games.Bethesda.Loot.Metadata.Yaml;

internal static class YamlText
{
    /// <summary>
    /// <paramref name="node"/> written as YAML without the document markers, for error messages.
    /// </summary>
    public static string Describe(YamlNode node)
    {
        if (node is YamlScalarNode scalar) return scalar.Value ?? "";

        var writer = new StringWriter { NewLine = "\n" };
        new YamlStream(new YamlDocument(node)).Save(writer, assignAnchors: false);

        return writer.ToString()
            .Replace("---\n", "", StringComparison.Ordinal)
            .Replace("...\n", "", StringComparison.Ordinal)
            .TrimEnd('\n');
    }
}
