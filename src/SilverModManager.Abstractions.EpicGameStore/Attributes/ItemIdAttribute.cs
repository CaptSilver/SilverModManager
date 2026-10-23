using SilverModManager.Abstractions.EpicGameStore.Values;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.ValueSerializers;

namespace SilverModManager.Abstractions.EpicGameStore.Attributes;

public sealed class ItemIdAttribute(string ns, string name) : ScalarAttribute<ItemId, string, Utf8InsensitiveSerializer>(ns, name)
{
    protected override string ToLowLevel(ItemId value) => value.Value;

    protected override ItemId FromLowLevel(string value, AttributeResolver resolver) => ItemId.From(value);
}
