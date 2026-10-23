using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.ValueSerializers;

namespace SilverModManager.Abstractions.NexusModsLibrary.Attributes;

/// <summary>
/// An attribute that holds a <see cref="RevisionId"/> value.
/// </summary>
public class RevisionIdAttribute(string ns, string name) : ScalarAttribute<RevisionId, ulong, UInt64Serializer>(ns, name)
{
    /// <inheritdoc />
    protected override ulong ToLowLevel(RevisionId value) => value.Value;

    /// <inheritdoc />
    protected override RevisionId FromLowLevel(ulong value, AttributeResolver resolver) => RevisionId.From(value);
}
