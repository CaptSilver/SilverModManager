using SilverModManager.Abstractions.GOG.Values;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.ValueSerializers;

namespace SilverModManager.Abstractions.Games.FileHashes.Attributes.Gog;

/// <summary>
/// An attribute for storing GOG product IDs.
/// </summary>
public class ProductIdAttribute(string ns, string name) : ScalarAttribute<ProductId, ulong, UInt64Serializer>(ns, name)
{
    protected override ulong ToLowLevel(ProductId value)
    {
        return value.Value;
    }

    protected override ProductId FromLowLevel(ulong value, AttributeResolver resolver)
    {
        return ProductId.From(value);
    }
}
