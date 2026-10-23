using SilverModManager.Abstractions.Steam.Values;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.ValueSerializers;

namespace SilverModManager.Abstractions.Games.FileHashes.Attributes.Steam;

/// <summary>
/// A manifest ID attribute.
/// </summary>
public class ManifestIdAttribute(string ns, string name) : ScalarAttribute<ManifestId, ulong, UInt64Serializer>(ns, name)
{
    protected override ulong ToLowLevel(ManifestId value)
    {
        return value.Value;
    }

    protected override ManifestId FromLowLevel(ulong value, AttributeResolver resolver)
    {
        return ManifestId.From(value);
    }
}
