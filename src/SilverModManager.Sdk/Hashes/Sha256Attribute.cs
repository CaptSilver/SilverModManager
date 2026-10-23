using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.ValueSerializers;

namespace SilverModManager.Sdk.Hashes;

/// <summary>
/// Attribute for <see cref="Sha256Value"/>. Stored as a 32 byte blob: there is no 256-bit
/// primitive serializer, and a blob keeps the value indexable without a hex round trip.
/// </summary>
public class Sha256Attribute(string ns, string name) : ScalarAttribute<Sha256Value, Memory<byte>, BlobSerializer>(ns, name)
{
    /// <inheritdoc/>
    protected override Memory<byte> ToLowLevel(Sha256Value value) => value.AsSpan().ToArray();

    /// <inheritdoc/>
    protected override Sha256Value FromLowLevel(Memory<byte> value, AttributeResolver resolver) => Sha256Value.From(value.Span);
}
