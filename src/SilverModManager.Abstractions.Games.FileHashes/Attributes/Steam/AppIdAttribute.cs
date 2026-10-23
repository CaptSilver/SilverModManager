using SilverModManager.Abstractions.Steam.Values;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.ValueSerializers;

namespace SilverModManager.Abstractions.Games.FileHashes.Attributes.Steam;

/// <summary>
/// An attribute for a Steam App ID.
/// </summary>
public class AppIdAttribute(string ns, string name) : ScalarAttribute<AppId, uint, UInt32Serializer>(ns, name) 
{
    protected override uint ToLowLevel(AppId value)
    {
        return value.Value;
    }

    protected override AppId FromLowLevel(uint value, AttributeResolver resolver)
    {
        return AppId.From(value);
    }
}
