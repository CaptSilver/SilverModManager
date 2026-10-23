using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions.ValueSerializers;

namespace SilverModManager.MnemonicDB.Abstractions.Attributes;

/// <summary>
/// An attribute that holds a float32 value.
/// </summary>
[PublicAPI]
public sealed class Float32Attribute(string ns, string name) : ScalarAttribute<float, float, Float32Serializer>(ns, name)
{
    /// <inheritdoc />
    protected override float ToLowLevel(float value) => value;

    /// <inheritdoc />
    protected override float FromLowLevel(float value, AttributeResolver resolver) => value;
}
