using System;
using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions.ValueSerializers;

namespace SilverModManager.MnemonicDB.Abstractions.Attributes;

/// <summary>
/// An attribute that holds a <see cref="Uri"/>.
/// </summary>
[PublicAPI]
public sealed class UriAttribute(string ns, string name) : ScalarAttribute<Uri, string, Utf8Serializer>(ns, name)
{
    /// <inheritdoc />
    protected override string ToLowLevel(Uri value) => value.ToString();

    /// <inheritdoc />
    protected override Uri FromLowLevel(string value, AttributeResolver resolver) => new(value);
}
