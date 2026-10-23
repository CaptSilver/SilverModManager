using SilverModManager.Abstractions.EpicGameStore.Values;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.ValueSerializers;

namespace SilverModManager.Abstractions.EpicGameStore.Attributes;

public sealed class BuildIdAttribute(string ns, string name) : ScalarAttribute<BuildId, string, Utf8InsensitiveSerializer>(ns, name)
{
    protected override string ToLowLevel(BuildId value) => value.Value;

    protected override BuildId FromLowLevel(string value, AttributeResolver resolver) => BuildId.From(value);
}
