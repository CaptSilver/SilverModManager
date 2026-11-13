using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Networking.NexusWebApi;

internal static class NexusModsFileCategories
{
    public static NexusModsFileCategory From(ModFileCategory category) => category switch
    {
        ModFileCategory.Main => NexusModsFileCategory.Main,
        ModFileCategory.Update => NexusModsFileCategory.Update,
        ModFileCategory.Optional => NexusModsFileCategory.Optional,
        ModFileCategory.OldVersion => NexusModsFileCategory.OldVersion,
        ModFileCategory.Miscellaneous => NexusModsFileCategory.Miscellaneous,
        ModFileCategory.Archived => NexusModsFileCategory.Archived,
        ModFileCategory.Removed => NexusModsFileCategory.Removed,
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown Nexus Mods file category"),
    };
}
