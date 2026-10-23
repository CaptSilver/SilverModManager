using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.ValueSerializers;

namespace SilverModManager.Sdk.Hashes;

/// <summary>
/// Stores a <see cref="Hash"/> as a <see cref="ulong"/>.
/// </summary>
public class HashAttribute(string ns, string name) : ScalarAttribute<Hash, ulong, UInt64Serializer>(ns, name)
{
    /// <inheritdoc />
    protected override ulong ToLowLevel(Hash value) => value.Value;

    /// <inheritdoc />
    protected override Hash FromLowLevel(ulong value, AttributeResolver resolver) => Hash.From(value);
}
