using SilverModManager.Abstractions.Steam.Values;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.ValueSerializers;

namespace SilverModManager.Abstractions.Games.FileHashes.Attributes.Steam;

/// <summary>
/// A <see cref="DepotId"/> attribute.
/// </summary>
public class DepotIdAttribute(string ns, string name) : ScalarAttribute<DepotId, uint, UInt32Serializer>(ns, name)
{
    protected override uint ToLowLevel(DepotId value)
    {
        return value.Value;
    }

    protected override DepotId FromLowLevel(uint value, AttributeResolver resolver)
    {
        return DepotId.From(value);
    }
}
