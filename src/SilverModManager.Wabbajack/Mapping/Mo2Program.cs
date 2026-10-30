using System.Text.RegularExpressions;
using NexusMods.Paths;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Wabbajack.Format;

namespace SilverModManager.Wabbajack.Mapping;

/// <summary>
/// Which archives of a list are Mod Organizer 2 itself and which of them the app can do without.
/// <see cref="NotNeeded"/> archives supply nothing but program files. <see cref="Kept"/> archives are Mod Organizer
/// releases too, but the list also takes a file from them for a mod or the game; the text says which.
/// </summary>
public sealed record Mo2ProgramArchives(IReadOnlySet<FileIdentity> NotNeeded, IReadOnlyDictionary<FileIdentity, string> Kept)
{
    public static readonly Mo2ProgramArchives None = new(new HashSet<FileIdentity>(), new Dictionary<FileIdentity, string>());

    /// <summary>Every archive recognised as a Mod Organizer release, needed or not.</summary>
    public bool IsRecognised(FileIdentity archive) => NotNeeded.Contains(archive) || Kept.ContainsKey(archive);
}

/// <summary>
/// A modlist is built in Mod Organizer 2 and ships MO2 itself, so the list names the release as an archive and its
/// directives lay the program (ModOrganizer.exe, its dlls, plugins, python) in the instance root. The app replaces
/// all of that, so those files are never installed and the archive is never fetched.
/// </summary>
public static partial class Mo2Program
{
    // "Mod.Organizer-2.4.4.7z", "Mod Organizer-2.5.0.zip", "ModOrganizer-2.4.4.7z": the name, then the version digit,
    // so a mod that only mentions Mod Organizer in its name is left alone.
    [GeneratedRegex(@"^Mod[ .]?Organizer[-_]v?\d", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseName();

    private static readonly string[] InstanceFolders = ["mods", "overwrite", "profiles", "TEMP_BSA_FILES", Mo2Layout.GameFolderFilesGroup];

    /// <summary>
    /// Whether a file the list puts at <paramref name="to"/> is part of the MO2 program or a tool folder, as opposed to
    /// the instance's mods and profiles or a copy of the game.
    /// </summary>
    public static bool IsProgramLocation(RelativePath to, IReadOnlySet<string> gameRootFolders)
    {
        var parts = to.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 1) return true;

        var folder = parts[0];
        if (InstanceFolders.Any(name => name.Equals(folder, StringComparison.OrdinalIgnoreCase))) return false;
        return !gameRootFolders.Any(name => name.Equals(folder, StringComparison.OrdinalIgnoreCase));
    }

    public static Mo2ProgramArchives Find(ModList list, IReadOnlySet<string> gameRootFolders)
    {
        var recognised = list.Archives.Where(a => ReleaseName().IsMatch(a.Name)).Select(a => a.XxHash64).ToHashSet();
        foreach (var directive in list.Directives)
        {
            // The program's executable at the root is as good as a name.
            if (directive is FromArchive from && from.To.ToString().Equals("ModOrganizer.exe", StringComparison.OrdinalIgnoreCase))
                recognised.Add(from.Source.ArchiveXxHash64);
        }
        if (recognised.Count == 0) return Mo2ProgramArchives.None;

        var needs = new Dictionary<FileIdentity, string>();
        foreach (var directive in list.Directives)
        {
            if (directive is not FromArchive from || !recognised.Contains(from.Source.ArchiveXxHash64)) continue;
            if (IsProgramLocation(from.To, gameRootFolders)) continue;
            needs.TryAdd(from.Source.ArchiveXxHash64, from.To.ToString());
        }

        var names = list.Archives.GroupBy(a => a.XxHash64).ToDictionary(g => g.Key, g => g.First().Name);
        return new Mo2ProgramArchives(
            recognised.Where(hash => !needs.ContainsKey(hash) && names.ContainsKey(hash)).ToHashSet(),
            needs.Where(n => names.ContainsKey(n.Key)).ToDictionary(n => n.Key,
                n => $"'{names[n.Key]}' is Mod Organizer 2, but the list takes '{n.Value}' from it, so it is still needed"));
    }
}
