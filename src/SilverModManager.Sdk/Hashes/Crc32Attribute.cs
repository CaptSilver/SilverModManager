using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.ValueSerializers;

namespace SilverModManager.Sdk.Hashes;

/// <summary>
/// Attribute for <see cref="Crc32Value"/>.
/// </summary>
public class Crc32Attribute(string ns, string name) : ScalarAttribute<Crc32Value, uint, UInt32Serializer>(ns, name)
{
    /// <inheritdoc/>
    protected override uint ToLowLevel(Crc32Value value) => value.Value;

    /// <inheritdoc/>
    protected override Crc32Value FromLowLevel(uint value, AttributeResolver resolver) => Crc32Value.From(value);
}
