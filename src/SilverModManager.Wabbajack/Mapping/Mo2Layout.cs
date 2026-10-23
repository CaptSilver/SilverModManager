using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Wabbajack.Mapping;

/// <summary>What the classifier needs to know about the game and the instance a modlist was built for.</summary>
/// <param name="SelectedProfile">The profile named in ModOrganizer.ini; every other profile's files are dropped.</param>
/// <param name="GameRootFolders">Top-level folders that hold a copy of the game ("Stock Game").</param>
/// <param name="ProfileSettingsFiles">The ini files MO2 keeps in a profile, with the place the game reads each from.</param>
/// <param name="ModFilePath">Where a file at this path inside a mod goes in the game.</param>
public sealed record Mo2Context(
    string SelectedProfile,
    IReadOnlySet<string> GameRootFolders,
    IReadOnlyList<GamePath> ProfileSettingsFiles,
    Func<RelativePath, GamePath> ModFilePath);

public enum SeedKind { ModlistTxt, PluginsTxt, LoadorderTxt, ModOrganizerIni }

/// <summary>Where a file of an MO2 instance goes in a loadout.</summary>
public abstract record Mo2Target
{
    private Mo2Target() { }

    /// <summary>A file of the mod (or of Overwrite) named <paramref name="GroupName"/>.</summary>
    public sealed record ModFile(string GroupName, GamePath Path) : Mo2Target;

    /// <summary>One of the profile's ini files, at the place the game reads it from.</summary>
    public sealed record ProfileSetting(GamePath Path) : Mo2Target;

    /// <summary>A file of a game-root folder, at <paramref name="Path"/> inside the game folder.</summary>
    public sealed record GameRootFile(string GroupName, RelativePath Path) : Mo2Target;

    /// <summary>A file that is read to order the loadout and is never deployed.</summary>
    public sealed record Seed(SeedKind Kind) : Mo2Target;

    /// <summary>A file that goes into the archive a CreateBSA directive builds.</summary>
    public sealed record BsaInput(RelativePath TempId, RelativePath Path) : Mo2Target;

    /// <summary>A file of a tools folder the loadout does not deploy; <paramref name="Path"/> is relative to the instance.</summary>
    public sealed record Extra(RelativePath Path) : Mo2Target;

    /// <summary>A file that belongs to Mod Organizer or to MO2's bookkeeping, which the app replaces.</summary>
    public sealed record Dropped(string Reason) : Mo2Target;
}

/// <summary>Sorts the destination paths of a modlist's directives by where they land in an MO2 instance.</summary>
public static class Mo2Layout
{
    public const string OverwriteGroup = "Overwrite";
    public const string ProfileSettingsGroup = "Profile settings";
    public const string GameFolderFilesGroup = "Game Folder Files";

    private static readonly HashSet<string> ModOrganizerFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "dlls", "plugins", "stylesheets", "translations", "tutorials", "licenses", "QtQml", "QtQuick.2", "platforms",
        "styles", "pythoncore", "resources", "NCC", "explorer++", "loot", "webcache", "logs", "downloads",
    };

    /// <summary>
    /// <paramref name="to"/> is a directive's destination with its path casing already unified
    /// (<see cref="PathCasing.Unify"/>); the rows are tried in order and the first match wins.
    /// </summary>
    public static Mo2Target Classify(RelativePath to, Mo2Context context)
    {
        var parts = to.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return new Mo2Target.Dropped("empty path");

        if (parts.Length == 1)
        {
            return parts[0].Equals("ModOrganizer.ini", StringComparison.OrdinalIgnoreCase)
                ? new Mo2Target.Seed(SeedKind.ModOrganizerIni)
                : new Mo2Target.Dropped("Mod Organizer file");
        }

        var folder = parts[0];
        if (folder.Equals("mods", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length < 3) return new Mo2Target.Dropped("Mod Organizer file");
            if (parts.Length == 3 && parts[2].Equals("meta.ini", StringComparison.OrdinalIgnoreCase))
                return new Mo2Target.Dropped("mod metadata");
            return ModFile(parts[1], parts, 2, context);
        }

        if (folder.Equals("overwrite", StringComparison.OrdinalIgnoreCase))
            return ModFile(OverwriteGroup, parts, 1, context);

        if (folder.Equals("profiles", StringComparison.OrdinalIgnoreCase))
            return ProfileFile(parts, context);

        if (folder.Equals("TEMP_BSA_FILES", StringComparison.OrdinalIgnoreCase))
        {
            return parts.Length < 3
                ? new Mo2Target.Dropped("Mod Organizer file")
                : new Mo2Target.BsaInput(RelativePath.FromUnsanitizedInput(parts[1]), Join(parts, 2));
        }

        var rootFolder = context.GameRootFolders.FirstOrDefault(name => name.Equals(folder, StringComparison.OrdinalIgnoreCase));
        if (rootFolder is not null)
            return new Mo2Target.GameRootFile(rootFolder, Join(parts, 1));
        if (folder.Equals(GameFolderFilesGroup, StringComparison.OrdinalIgnoreCase))
            return new Mo2Target.GameRootFile(GameFolderFilesGroup, Join(parts, 1));

        if (ModOrganizerFolders.Contains(folder)) return new Mo2Target.Dropped("Mod Organizer");
        if (parts[^1].EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) return new Mo2Target.Dropped("download metadata");
        return new Mo2Target.Extra(to);
    }

    private static Mo2Target ModFile(string group, string[] parts, int first, Mo2Context context)
    {
        // Root Builder's convention: a mod's Root folder is laid over the game folder rather than Data.
        if (parts.Length > first + 1 && parts[first].Equals("Root", StringComparison.OrdinalIgnoreCase))
            return new Mo2Target.ModFile(group, new GamePath(LocationId.Game, Join(parts, first + 1)));
        return new Mo2Target.ModFile(group, context.ModFilePath(Join(parts, first)));
    }

    private static Mo2Target ProfileFile(string[] parts, Mo2Context context)
    {
        if (parts.Length < 3) return new Mo2Target.Dropped("profile file");
        if (!parts[1].Equals(context.SelectedProfile, StringComparison.OrdinalIgnoreCase))
            return new Mo2Target.Dropped("other profile");
        if (parts.Length > 3) return new Mo2Target.Dropped("profile file");

        var name = parts[2];
        if (name.Equals("modlist.txt", StringComparison.OrdinalIgnoreCase)) return new Mo2Target.Seed(SeedKind.ModlistTxt);
        if (name.Equals("plugins.txt", StringComparison.OrdinalIgnoreCase)) return new Mo2Target.Seed(SeedKind.PluginsTxt);
        if (name.Equals("loadorder.txt", StringComparison.OrdinalIgnoreCase)) return new Mo2Target.Seed(SeedKind.LoadorderTxt);

        foreach (var setting in context.ProfileSettingsFiles)
        {
            if (setting.FileName.ToString().Equals(name, StringComparison.OrdinalIgnoreCase))
                return new Mo2Target.ProfileSetting(setting);
        }
        return new Mo2Target.Dropped("profile file");
    }

    private static RelativePath Join(string[] parts, int skip) => RelativePath.FromUnsanitizedInput(string.Join('/', parts.Skip(skip)));
}
