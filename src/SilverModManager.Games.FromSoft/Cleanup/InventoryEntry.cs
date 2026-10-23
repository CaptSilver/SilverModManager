namespace SilverModManager.Games.FromSoft.Cleanup;

/// <summary>What an entry in the game folder is. A link is listed as itself and never followed.</summary>
public enum InventoryKind
{
    File,
    Directory,
    Link,
}

/// <param name="Path">The path relative to the <c>Game</c> folder, with <c>/</c> separators.</param>
/// <param name="Kind">Whether it is a file, a folder or a symbolic link.</param>
/// <param name="Size">The size of a file; 0 for anything else.</param>
/// <param name="LinkTarget">Where a link points, as stored in the link; null for anything else.</param>
public sealed record InventoryEntry(string Path, InventoryKind Kind, long Size, string? LinkTarget = null);
