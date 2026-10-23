using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.ValueSerializers;

namespace SilverModManager.Abstractions.NexusModsLibrary.Attributes;

/// <summary>
/// An attribute that holds a <see cref="CollectionId"/> value.
/// </summary>
public class CollectionIdAttribute(string ns, string name) : ScalarAttribute<CollectionId, ulong, UInt64Serializer>(ns, name)
{
    /// <inheritdoc />
    protected override ulong ToLowLevel(CollectionId value) => value.Value;

    /// <inheritdoc />
    protected override CollectionId FromLowLevel(ulong value, AttributeResolver resolver) => CollectionId.From(value);
}
