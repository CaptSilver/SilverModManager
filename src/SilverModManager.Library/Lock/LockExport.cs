using System.Text;
using JetBrains.Annotations;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.Library.Sources;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.Library.Lock;

/// <summary>
/// Turns the mods of a loadout into a <see cref="LockFile"/>.
/// </summary>
[PublicAPI]
public sealed class LockExport(IEnumerable<ILibrarySource> sources, LibraryFileDigests digests)
{
    /// <summary>
    /// Exports every enabled group that links a file one of the library sources owns. A group that links anything else
    /// fails the export: a lock that quietly left a mod out would be worse than none. The exception is a dangling
    /// file (no SHA-256 and no bytes in the file store, such as an unfinished download's record): nothing can pin it,
    /// so it is left out and its message added to <paramref name="skipped"/>.
    /// </summary>
    public async ValueTask<LockFile> FromLoadout(Loadout.ReadOnly loadout, CancellationToken cancellationToken = default, ICollection<string>? skipped = null)
    {
        var entries = new Dictionary<string, LockEntry>(StringComparer.Ordinal);

        var linked = LibraryLinkedLoadoutItem.All(loadout.Rebase().Db)
            .Where(item => item.AsLoadoutItem().LoadoutId == loadout.LoadoutId && item.AsLoadoutItem().IsEnabled())
            .OrderBy(item => item.AsLoadoutItem().Name, StringComparer.Ordinal);

        foreach (var item in linked)
        {
            var groupName = item.AsLoadoutItem().Name;
            var libraryItem = item.LibraryItem;
            if (!libraryItem.TryGetAsLibraryFile(out var file))
                throw new LockException($"{MarkdownText.Code(groupName)} is not linked to a downloaded file, so there is nothing to pin.");

            LockEntry entry;
            try
            {
                entry = await ToEntry(groupName, libraryItem, file, cancellationToken);
            }
            catch (LockException)
            {
                if (!await digests.IsDangling(file)) throw;

                skipped?.Add($"{MarkdownText.Code(groupName)} ({MarkdownText.Code(file.FileName)}) has no SHA-256 and its bytes are not in the file store, so it cannot be pinned.");
                continue;
            }

            if (!entries.TryAdd(entry.Id, entry))
                throw new LockException($"{MarkdownText.Code(groupName)} ({MarkdownText.Code(file.FileName)}) and {MarkdownText.Code(entries[entry.Id].Asset)} both become the lock id {MarkdownText.Code(entry.Id)}.");
        }

        return new LockFile(entries.Values.ToArray());
    }

    // The first source that owns the item pins it, so registration order decides when two could.
    private async ValueTask<LockEntry> ToEntry(string groupName, LibraryItem.ReadOnly libraryItem, LibraryFile.ReadOnly file, CancellationToken cancellationToken)
    {
        foreach (var source in sources)
        {
            if (await source.Describe(groupName, libraryItem, file, cancellationToken) is { } entry)
                return LockEntry.From(entry);
        }

        var names = string.Join(", ", sources.Select(source => source.Name));
        throw new LockException($"{MarkdownText.Code(file.FileName)} ({MarkdownText.Code(groupName)}) came from none of the sources a lock can pin ({MarkdownText.Code(names)}), so it cannot be pinned.");
    }

    /// <summary>
    /// A lower case id made of letters, digits and single dashes, which is a bare TOML key and reads the same in erm.
    /// </summary>
    internal static string SanitizeId(string name, string file)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c)) builder.Append(c);
            else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
        }

        var id = builder.ToString().TrimEnd('-');
        return id.Length > 0 ? id : throw new LockException($"{MarkdownText.Code(name)} ({MarkdownText.Code(file)}) has no letters or digits to make a lock id from.");
    }
}
