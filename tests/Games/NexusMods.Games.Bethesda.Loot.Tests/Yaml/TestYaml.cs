using YamlDotNet.RepresentationModel;

namespace NexusMods.Games.Bethesda.Loot.Tests.Yaml;

internal static class TestYaml
{
    /// <summary>The root node of the only document in <paramref name="yaml"/>.</summary>
    public static YamlNode Parse(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        return stream.Documents.Single().RootNode;
    }
}
