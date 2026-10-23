using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions.ValueSerializers;

namespace SilverModManager.MnemonicDB.Abstractions.Attributes;

/// <summary>
/// An attribute that encodes a symbol.
/// </summary>
[PublicAPI]
public sealed class SymbolAttribute(string ns, string name) : ScalarAttribute<Symbol, string, AsciiSerializer>(ns, name)
{
    /// <inheritdoc />
    protected override string ToLowLevel(Symbol value) => value.Id;

    /// <inheritdoc />
    protected override Symbol FromLowLevel(string value, AttributeResolver resolver) => Symbol.InternPreSanitized(value);
}

