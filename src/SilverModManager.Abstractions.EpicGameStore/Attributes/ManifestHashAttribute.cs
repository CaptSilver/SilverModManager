using SilverModManager.Abstractions.EpicGameStore.Values;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.ValueSerializers;

namespace SilverModManager.Abstractions.EpicGameStore.Attributes;

public sealed class ManifestHashAttribute(string ns, string name) : ScalarAttribute<ManifestHash, string, Utf8InsensitiveSerializer>(ns, name)
{
    protected override string ToLowLevel(ManifestHash value) => value.Value;

    protected override ManifestHash FromLowLevel(string value, AttributeResolver resolver) => ManifestHash.From(value);
}
