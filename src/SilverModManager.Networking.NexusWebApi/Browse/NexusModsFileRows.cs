using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Networking.NexusWebApi;

/// <summary>Maps the API's file fragment onto the row a mod's Files tab shows.</summary>
public static class NexusModsFileRows
{
    public static NexusModsFileRow From(IModFile file) => new(
        FileUid.FromV2Api(file.Uid).FileId,
        file.Name,
        file.Version,
        NexusModsFileCategories.From(file.Category),
        long.TryParse(file.SizeInBytes, out var size) ? size : 0,
        DateTimeOffset.FromUnixTimeSeconds(file.Date));
}
