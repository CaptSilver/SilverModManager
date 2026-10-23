using JetBrains.Annotations;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.Library.Lock;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Hashes;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.NexusModsApi;

namespace NexusMods.Library.Sources;

/// <summary>
/// One pinned file of a <see cref="ILibrarySource"/>. It maps one to one onto a <see cref="LockEntry"/>; the fields have
/// the same meaning.
/// </summary>
[PublicAPI]
public sealed record LibrarySourceEntry(string Id, string Version, string? FileId, string Asset, string Sha256, string Source);

/// <summary>
/// What a source may need to fetch a file beyond the entry: the database, the game of the loadout being imported
/// into, and the Nexus Mods id of that game (null when the game has none or no loadout is involved).
/// </summary>
[PublicAPI]
public sealed record LibrarySourceContext(IConnection Connection, IGameData? Game, NexusModsGameId? NexusGame);

/// <summary>
/// A place library files come from that a lock can name: it knows how to recognise its own files in a loadout, pin
/// them, and fetch them again.
/// </summary>
[PublicAPI]
public interface ILibrarySource
{
    /// <summary>
    /// The value of the <c>source</c> key of the lock entries this source writes and reads.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Pins the file of a loadout group, or returns null when this source does not own the item. Throws
    /// <see cref="LockException"/> when the source does own it but cannot pin it. Async because a digest may have to
    /// be computed from the file store first.
    /// </summary>
    ValueTask<LibrarySourceEntry?> Describe(string groupName, LibraryItem.ReadOnly item, LibraryFile.ReadOnly file, CancellationToken cancellationToken);

    /// <summary>
    /// Why <see cref="Download"/> cannot even be attempted for this entry, or null when it can.
    /// </summary>
    string? WhyNotDownloadable(LibrarySourceEntry entry, LibrarySourceContext context);

    /// <summary>
    /// Fetches the file and adds it to the library. A file that does not match <paramref name="expected"/> must not be
    /// left in the library.
    /// </summary>
    ValueTask<LibraryFile.ReadOnly> Download(LibrarySourceEntry entry, Sha256Value expected, LibrarySourceContext context, CancellationToken cancellationToken);
}
