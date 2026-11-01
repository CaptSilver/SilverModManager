using System.Collections.Concurrent;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.Wabbajack.Library;

/// <summary>
/// A file fetched for a modlist. It is for the game the list is built for, which is the only game the file is known
/// to be needed by: the Nexus Mods domain of its archives is shared by sibling games (Skyrim SE and Skyrim VR).
/// A missing list, or one for a game this app has no title for, answers unknown.
/// </summary>
[PublicAPI]
public sealed record ModlistAcquisition(LibraryFileId? Modlist, ILogger? Logger = null) : IAcquisitionContext
{
    // A list for an untitled game is asked about once per archive; saying so once per list is enough.
    private static readonly ConcurrentDictionary<EntityId, byte> Warned = new();

    /// <inheritdoc />
    public Destination Destination(IDb db)
    {
        if (Modlist is not { } id) return Sdk.Library.Destination.Unknown;
        var list = WabbajackModlistLibraryFile.Load(db, id.Value);
        return list.IsValid() ? ForList(id.Value, list.GameType, Logger) : Sdk.Library.Destination.Unknown;
    }

    /// <summary>The destination of the files of a list with this <paramref name="gameType"/>.</summary>
    internal static Destination ForList(EntityId list, string gameType, ILogger? logger)
    {
        if (WabbajackGames.GameIdFor(gameType) is { } game) return Sdk.Library.Destination.Of(game);

        if (logger is not null && Warned.TryAdd(list, 0))
            logger.LogWarning("Modlist {List} is for '{GameType}', a game this app has no title for, so its files are not assigned a game", list, gameType);
        return Sdk.Library.Destination.Unknown;
    }
}
