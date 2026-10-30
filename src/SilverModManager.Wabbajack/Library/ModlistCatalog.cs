using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.Wabbajack.Library;

/// <summary>A modlist in the library, with what the command line prints to tell one from another.</summary>
public sealed record ModlistInfo(LibraryFileId Id, string Name, string Version, string GameType, int ArchiveCount, string FileName)
{
    /// <summary>The id as the listing prints it and <see cref="IModlistCatalog.Find"/> reads it back.</summary>
    public string HexId => Id.Value.Value.ToString("X");
}

/// <summary>The modlists the library holds, found by name for the command line.</summary>
public interface IModlistCatalog
{
    IReadOnlyList<ModlistInfo> List();

    /// <summary>
    /// The modlist <paramref name="query"/> names: its hex id, its name (any case), or a <c>.wabbajack</c> path or file
    /// name of a list already in the library.
    /// </summary>
    /// <exception cref="ModlistNotKnownException">Nothing matches, or the name fits more than one list.</exception>
    ModlistInfo Find(string query);
}

/// <summary>The command line named a modlist the app does not hold, or one it cannot tell apart from another.</summary>
public sealed class ModlistNotKnownException(string message) : UserFacingException(message)
{
    public override string Title => "Modlist not found";
}

public sealed class ModlistCatalog(IConnection connection) : IModlistCatalog
{
    public IReadOnlyList<ModlistInfo> List()
    {
        var db = connection.Db;
        return WabbajackModlistLibraryFile.All(db)
            .Select(Describe)
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Version, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Id.Value.Value)
            .ToArray();
    }

    public ModlistInfo Find(string query)
    {
        var trimmed = query.Trim();
        var all = List();

        if (EntityId.TryParseFromHex(trimmed, out var id) && all.FirstOrDefault(m => m.Id.Value == id) is { } byId)
            return byId;

        var byName = all.Where(m => string.Equals(m.Name, trimmed, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (byName.Length == 0)
        {
            var fileName = FileNameOf(trimmed);
            byName = all.Where(m => string.Equals(m.FileName, fileName, StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        return byName.Length switch
        {
            1 => byName[0],
            0 => throw new ModlistNotKnownException($"'{query}' is not a modlist the app knows. Add the .wabbajack file to the library first; 'wabbajack list' shows what it holds."),
            _ => throw new ModlistNotKnownException(
                $"More than one modlist fits '{query}': {string.Join("; ", byName.Select(m => $"{m.Name} {m.Version} ({m.FileName}), id {m.HexId}"))}. Use the id."),
        };
    }

    // A path from another machine's convention still ends in the file name.
    private static string FileNameOf(string path) => path[(path.LastIndexOfAny(['/', '\\']) + 1)..];

    private static ModlistInfo Describe(WabbajackModlistLibraryFile.ReadOnly modlist) => new(
        LibraryFileId.From(modlist.Id), modlist.Name, modlist.Version, modlist.GameType, modlist.ArchiveCount, modlist.AsLibraryFile().FileName.ToString());
}
