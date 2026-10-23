using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.ValueSerializers;

namespace SilverModManager.Sdk.Hashes;

/// <summary>
/// Attribute for <see cref="Md5Value"/>.
/// </summary>
public class Md5Attribute(string ns, string name) : ScalarAttribute<Md5Value, UInt128, UInt128Serializer>(ns, name)
{
    /// <inheritdoc/>
    protected override UInt128 ToLowLevel(Md5Value value) => value.AsUInt128();

    /// <inheritdoc/>
    protected override Md5Value FromLowLevel(UInt128 value, AttributeResolver resolver) => Md5Value.From(value);
}
