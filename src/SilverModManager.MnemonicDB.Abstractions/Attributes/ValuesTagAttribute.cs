using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.ValueSerializers;

namespace SilverModManager.MnemonicDB.Abstractions.Attributes;

/// <summary>
/// An attribute that represents a value tag value
/// </summary>
[PublicAPI]
public sealed class ValuesTagAttribute(string ns, string name) : ScalarAttribute<ValueTag, byte, UInt8Serializer>(ns, name)
{
    /// <inheritdoc />
    protected override byte ToLowLevel(ValueTag value) => (byte)value;

    /// <inheritdoc />
    protected override ValueTag FromLowLevel(byte value, AttributeResolver resolver) => (ValueTag)value;
}
