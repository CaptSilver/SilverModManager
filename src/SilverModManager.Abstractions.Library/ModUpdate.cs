using SilverModManager.Sdk.Library;

namespace SilverModManager.Abstractions.Library;

/// <summary>
/// A newer version for one or more library items. What the source needs to apply it travels in the subclass its
/// own <see cref="IModSource"/> defines; everything else sees only these fields.
/// </summary>
/// <param name="CurrentItems">The library items the update would replace.</param>
/// <param name="CurrentVersion">
/// The version shown next to the new one on a parent row: for a Nexus mod page, the version of the file whose
/// update is newest.
/// </param>
/// <param name="NewestVersion">The version the update brings.</param>
/// <param name="PublishedAt">When the newest version was published, if the source knows.</param>
public abstract record ModUpdate(
    IReadOnlyList<LibraryItemId> CurrentItems,
    string CurrentVersion,
    string NewestVersion,
    DateTimeOffset? PublishedAt)
{
    /// <summary>
    /// How many updates this is, shown on the Update button. A source whose items and updates are not one to one
    /// overrides it (Nexus counts files, and a file held twice is still one update).
    /// </summary>
    public virtual int Count => CurrentItems.Count;
}
