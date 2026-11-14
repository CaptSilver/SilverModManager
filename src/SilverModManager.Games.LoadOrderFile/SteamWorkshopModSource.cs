using NexusMods.Paths;
using SilverModManager.Games.LoadOrderFile.Naming;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.LoadOrderFile;

/// <summary>
/// The mods a Steam user subscribed to: <c>steamapps/workshop/content/&lt;app id&gt;/&lt;item id&gt;</c> beside the
/// game's <c>steamapps/common</c> folder, each named by <paramref name="namer"/>. Found only for an install in a
/// library laid out that way.
/// </summary>
public sealed class SteamWorkshopModSource(uint appId, IExternalModNamer namer, ExternalListing listing = ExternalListing.WhenListed) : IExternalModSource
{
    public ExternalListing Listing => listing;

    public IReadOnlyList<string> Find(AbsolutePath gameFolder)
        => Items(gameFolder).SelectMany(namer.Names).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The folder name of each mod is the item's Steam id.</summary>
    public IReadOnlyList<ExternalMod> FindMods(AbsolutePath gameFolder) => ExternalMod.Read(namer, Items(gameFolder));

    private IEnumerable<AbsolutePath> Items(AbsolutePath gameFolder) => WorkshopFolder.Items(gameFolder, appId).Select(item => item.Folder);
}
