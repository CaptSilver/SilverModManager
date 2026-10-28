using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.ValueSerializers;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Games.FileHashes.Attributes;

/// <summary>
/// A <see cref="LocationId"/>, stored as the ushort the id wraps.
/// </summary>
public sealed class LocationIdAttribute(string ns, string name) : ScalarAttribute<LocationId, ushort, UInt16Serializer>(ns, name)
{
    /// <inheritdoc />
    protected override ushort ToLowLevel(LocationId value) => value.Value;

    /// <inheritdoc />
    protected override LocationId FromLowLevel(ushort value, AttributeResolver resolver) => LocationId.From(value);
}
