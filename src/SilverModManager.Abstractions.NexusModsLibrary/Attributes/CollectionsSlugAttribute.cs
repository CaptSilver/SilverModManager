using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.ValueSerializers;

namespace SilverModManager.Abstractions.NexusModsLibrary.Attributes;

/// <summary>
/// An attribute that holds a <see cref="CollectionSlug"/> value.
/// </summary>
public class CollectionsSlugAttribute(string ns, string name) : ScalarAttribute<CollectionSlug, string, AsciiSerializer>(ns, name)
{
    /// <inheritdoc />
    protected override string ToLowLevel(CollectionSlug value) => value.Value;

    /// <inheritdoc />
    protected override CollectionSlug FromLowLevel(string value, AttributeResolver resolver) => CollectionSlug.From(value);
}
