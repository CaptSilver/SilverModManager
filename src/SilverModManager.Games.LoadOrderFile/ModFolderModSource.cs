using NexusMods.Paths;
using SilverModManager.Games.LoadOrderFile.Naming;

namespace SilverModManager.Games.LoadOrderFile;

/// <summary>
/// The folders already in the game's own mod folder, from before the loadout or put there by hand. The loadout only
/// manages what it installed, so these are listed by <paramref name="namer"/> and kept as the order file has them.
/// </summary>
public sealed class ModFolderModSource(RelativePath modFolder, IExternalModNamer namer, ExternalListing listing) : IExternalModSource
{
    public ExternalListing Listing => listing;

    public IReadOnlyList<string> Find(AbsolutePath gameFolder)
        => Folders(gameFolder).SelectMany(namer.Names).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public IReadOnlyList<ExternalMod> FindMods(AbsolutePath gameFolder) => ExternalMod.Read(namer, Folders(gameFolder));

    private IEnumerable<AbsolutePath> Folders(AbsolutePath gameFolder)
    {
        var folder = gameFolder.Combine(modFolder);
        return folder.DirectoryExists()
            ? folder.EnumerateDirectories(recursive: false).OrderBy(mod => mod.FileName, StringComparer.Ordinal)
            : [];
    }
}
