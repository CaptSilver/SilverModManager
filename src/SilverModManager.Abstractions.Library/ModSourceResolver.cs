using SilverModManager.Sdk.Library;

namespace SilverModManager.Abstractions.Library;

/// <summary>
/// Finds the <see cref="IModSource"/> that owns a library item.
/// </summary>
public sealed class ModSourceResolver(IEnumerable<IModSource> sources)
{
    private readonly IModSource[] _sources = sources.ToArray();

    /// <summary>
    /// Every source, for the things that ask all of them (checking for updates).
    /// </summary>
    public IReadOnlyList<IModSource> Sources => _sources;

    /// <summary>
    /// The source that owns the item, or null for an item no source owns.
    /// </summary>
    public IModSource? For(LibraryItem.ReadOnly item) => _sources.FirstOrDefault(source => source.Owns(item));
}
