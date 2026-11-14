using SilverModManager.Abstractions.Library;
using SilverModManager.Library.GitHub;
using SilverModManager.Networking.GitHub.DTOs;
using SilverModManager.Sdk.Library;

// Not SilverModManager.Library.ModSources: App.UI has a static class ModSources and the names would clash.
namespace SilverModManager.Library;

/// <summary>
/// A newer release of one GitHub repository and the library items downloaded from older releases of it.
/// </summary>
internal sealed record GitHubModUpdate : ModUpdate
{
    /// <summary>The shelf repository the items came from, which says which asset of the new release to take.</summary>
    public ShelfRepository Repository { get; }

    public Release Newest { get; }

    private GitHubModUpdate(ShelfRepository repository, Release newest, IReadOnlyList<LibraryItemId> items, string current)
        : base(items, current, newest.TagName, newest.PublishedAt)
    {
        Repository = repository;
        Newest = newest;
    }

    /// <summary>
    /// None when there are no items. The version shown as current is the highest of the ones the items hold.
    /// </summary>
    public static GitHubModUpdate? From(ShelfRepository repository, Release newest, IReadOnlyList<(LibraryItem.ReadOnly Item, string Tag)> items)
    {
        if (items.Count == 0) return null;

        var current = items[0].Tag;
        foreach (var (_, tag) in items)
        {
            if (ReleaseVersion.TryParse(tag, out var version) && ReleaseVersion.TryParse(current, out var best) && version > best) current = tag;
        }

        return new GitHubModUpdate(repository, newest, [.. items.Select(static item => LibraryItemId.From(item.Item.Id))], current);
    }
}
