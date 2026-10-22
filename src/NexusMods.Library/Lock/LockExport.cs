using System.Text;
using JetBrains.Annotations;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Extensions;
using NexusMods.Library.Sources;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Library.Lock;

/// <summary>
/// Turns the mods of a loadout into a <see cref="LockFile"/>.
/// </summary>
[PublicAPI]
public sealed class LockExport(IEnumerable<ILibrarySource> sources)
{
    /// <summary>
    /// Exports every enabled group that links a file one of the library sources owns. A group that links anything else, or
    /// a file whose digest cannot be established, fails the export: a lock that quietly left a mod out, or pinned it
    /// without a digest, would be worse than none.
    /// </summary>
    public async ValueTask<LockFile> FromLoadout(Loadout.ReadOnly loadout, CancellationToken cancellationToken = default)
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
                throw new LockException($"'{groupName}' is not linked to a downloaded file, so there is nothing to pin.");

            var entry = await ToEntry(groupName, libraryItem, file, cancellationToken);
            if (!entries.TryAdd(entry.Id, entry))
                throw new LockException($"'{groupName}' ({file.FileName}) and '{entries[entry.Id].Asset}' both become the lock id '{entry.Id}'.");
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
        throw new LockException($"'{file.FileName}' ('{groupName}') came from none of the sources a lock can pin ({names}), so it cannot be pinned.");
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
        return id.Length > 0 ? id : throw new LockException($"'{name}' ({file}) has no letters or digits to make a lock id from.");
    }
}
