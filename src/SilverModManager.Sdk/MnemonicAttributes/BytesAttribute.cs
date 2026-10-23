using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.ValueSerializers;

namespace SilverModManager.Sdk.MnemonicAttributes;

/// <summary>
/// Bytes.
/// </summary>
public class BytesAttribute(string ns, string name) : ScalarAttribute<Memory<byte>, Memory<byte>, BlobSerializer>(ns, name)
{
    /// <inheritdoc />
    protected override Memory<byte> ToLowLevel(Memory<byte> value) => value;

    /// <inheritdoc />
    protected override Memory<byte> FromLowLevel(Memory<byte> value, AttributeResolver resolver) => value;
}
