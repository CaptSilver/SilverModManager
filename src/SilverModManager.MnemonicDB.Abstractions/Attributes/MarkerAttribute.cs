using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.ValueSerializers;

namespace SilverModManager.MnemonicDB.Abstractions.Attributes;

/// <summary>
/// An attribute that doesn't have a value, but is used to dispatch logic or to mark an entity
/// as being of a certain type.
/// </summary>
[PublicAPI]
public sealed class MarkerAttribute(string ns, string name) : ScalarAttribute<Null, Null, NullSerializer>(ns, name)
{
    /// <inheritdoc />
    protected override Null ToLowLevel(Null value) => value;

    /// <inheritdoc />
    protected override Null FromLowLevel(Null value, AttributeResolver resolver) => value;
}
