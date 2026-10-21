using NexusMods.Games.Declarative.Schema;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace NexusMods.Games.Declarative.Capabilities;

/// <summary>Types the raw YAML block of a capability.</summary>
public static class CapabilityConfig
{
    private static readonly ISerializer Writer = new SerializerBuilder().Build();

    private static readonly IDeserializer Reader = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    /// <summary>
    /// Deserialises <paramref name="block"/> into <paramref name="configType"/>, strictly like the
    /// definition itself. A block with no content (<c>creationEngine:</c> alone) binds to a default instance.
    /// </summary>
    /// <exception cref="DefinitionException">The block doesn't fit the type.</exception>
    public static object Bind(object? block, Type configType)
    {
        if (block is null) return Activator.CreateInstance(configType) ?? throw new DefinitionException($"{configType} cannot be created without a config block");
        try
        {
            return Reader.Deserialize(Writer.Serialize(block), configType)
                ?? throw new DefinitionException($"the config block is empty for {configType}");
        }
        catch (YamlException e)
        {
            throw new DefinitionException(e.Message, e);
        }
    }

    public static TConfig Bind<TConfig>(object? block) where TConfig : class => (TConfig)Bind(block, typeof(TConfig));
}
