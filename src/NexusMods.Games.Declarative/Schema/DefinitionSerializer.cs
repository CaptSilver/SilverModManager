using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace NexusMods.Games.Declarative.Schema;

/// <summary>
/// Reads and writes definition YAML. Reading is strict: a key the schema doesn't know is an error,
/// so a typo in a hand-written definition isn't silently ignored.
/// </summary>
public static class DefinitionSerializer
{
    private static readonly IDeserializer Reader = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    private static readonly ISerializer Writer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitEmptyCollections)
        .Build();

    /// <exception cref="DefinitionException">The text is not a definition.</exception>
    public static GameDefinition Read(string yaml)
    {
        try
        {
            return Reader.Deserialize<GameDefinition?>(yaml) ?? throw new DefinitionException("the file is empty");
        }
        catch (YamlException e)
        {
            throw new DefinitionException(e.Message, e);
        }
    }

    public static string Write(GameDefinition definition) => Writer.Serialize(definition);
}
