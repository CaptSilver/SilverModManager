using NexusMods.Paths;

namespace SilverModManager.Games.Larian.Engine;

/// <summary>Works out which player profile folder a game's mod list is written into.</summary>
public static class LarianProfileResolver
{
    /// <summary>The name of the mod list inside a profile folder.</summary>
    public const string ModsettingsFileName = "modsettings.lsx";

    /// <summary>
    /// The profile folder, relative to <paramref name="profilesLocation"/>: empty when the location is the profile
    /// itself. With one folder per profile that is the only folder, or else the one the player named in
    /// <paramref name="settingValue"/>; null when there is no folder or the choice is open.
    /// </summary>
    public static RelativePath? Resolve(LarianProfileLayout layout, AbsolutePath profilesLocation, string settingValue)
    {
        if (layout is LarianProfileLayout.Fixed) return default(RelativePath);

        var folders = ProfileFolders(profilesLocation);
        if (folders.Count == 1) return RelativePath.FromUnsanitizedInput(folders[0]);

        // The player typed the name, so a different case still names the folder.
        var named = folders.FirstOrDefault(f => f.Equals(settingValue, StringComparison.Ordinal))
                    ?? folders.FirstOrDefault(f => f.Equals(settingValue, StringComparison.OrdinalIgnoreCase));
        if (named is null) return null;
        return RelativePath.FromUnsanitizedInput(named);
    }

    /// <summary>How many profile folders there are: a folder counts whether or not it holds a mod list yet.</summary>
    public static int CountProfiles(AbsolutePath profilesLocation) => ProfileFolders(profilesLocation).Count;

    private static List<string> ProfileFolders(AbsolutePath profilesLocation) =>
        profilesLocation.DirectoryExists()
            ? profilesLocation.EnumerateDirectories(recursive: false).Select(d => d.FileName).Order(StringComparer.Ordinal).ToList()
            : [];
}
