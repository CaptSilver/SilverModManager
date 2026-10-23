using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.ValueSerializers;

namespace SilverModManager.Abstractions.NexusModsLibrary.Attributes;

/// <summary>
/// An attribute that holds a <see cref="RevisionNumber"/> value.
/// </summary>
public class RevisionNumberAttribute(string ns, string name) : ScalarAttribute<RevisionNumber, ulong, UInt64Serializer>(ns, name)
{
    /// <inheritdoc />
    protected override ulong ToLowLevel(RevisionNumber value) => value.Value;

    /// <inheritdoc />
    protected override RevisionNumber FromLowLevel(ulong value, AttributeResolver resolver) => RevisionNumber.From(value);
}
