using NexusMods.Paths;
using SilverModManager.Games.LoadOrderFile.Manifests;
using SilverModManager.Games.LoadOrderFile.Naming;

namespace SilverModManager.Games.LoadOrderFile;

/// <summary>
/// The mods a game ships in its own data folder (RimWorld's Core and expansions), each named by its manifest. A
/// <paramref name="filter"/> picks which of the folder's mods this source stands for, so the game's base mod and its
/// expansions can follow different <see cref="ExternalListing"/> rules.
/// </summary>
public sealed class GameDataFolderModSource(RelativePath dataFolder, IModManifestReader reader, Func<string, bool> filter, ExternalListing listing) : IExternalModSource
{
    private readonly ManifestNamer _namer = new(reader);

    public ExternalListing Listing => listing;

    public IReadOnlyList<string> Find(AbsolutePath gameFolder)
        => Folders(gameFolder).SelectMany(_namer.Names).Where(filter).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public IReadOnlyList<ExternalMod> FindMods(AbsolutePath gameFolder)
        => ExternalMod.Read(_namer, Folders(gameFolder)).Where(mod => filter(mod.Manifest.Id)).ToList();

    private IEnumerable<AbsolutePath> Folders(AbsolutePath gameFolder)
    {
        var folder = gameFolder.Combine(dataFolder);
        return folder.DirectoryExists()
            ? folder.EnumerateDirectories(recursive: false).OrderBy(mod => mod.FileName, StringComparer.Ordinal)
            : [];
    }
}
