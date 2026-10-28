using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Witcher.Witcher3.Installers;

internal enum ContentFolderKind { None, Dlc, Mod }

/// <summary>Where one file of an archive goes.</summary>
internal readonly record struct Placement(string Source, GamePath Target);

/// <summary>
/// Decides where a Witcher 3 archive's files go. The rules follow Vortex's installers: the first one whose
/// pattern matches claims the archive and places the files it understands; the rest are left for
/// <see cref="Diagnostics.UnplacedArchiveFilesEmitter"/> to report.
/// </summary>
internal static class Witcher3InstallPlan
{
    private const string PartSuffix = ".part.txt";
    private const string Content = "content";

    private static readonly string MenuConfigFolder = WitcherConstants.W3MenuConfigFolder.Path.Path;

    /// <returns>The placements of the first rule that claims the archive, or null when none does.</returns>
    public static IReadOnlyList<Placement>? Plan(IReadOnlyList<string> archivePaths, string archiveFileName)
    {
        var paths = archivePaths.Select(p => new ArchivePath(p)).ToArray();
        var modName = Witcher3ModInstaller.ModNameFromArchive(archiveFileName);

        return MenuMod(paths, modName)
               ?? Mixed(paths)
               ?? TopLevelMods(paths)
               ?? BareContent(paths, modName)
               ?? Dlc(paths);
    }

    private static Placement[]? MenuMod(ArchivePath[] paths, string archiveModName)
    {
        if (!paths.Any(p => p.Lower.Contains(MenuConfigFolder, StringComparison.Ordinal) || p.Lower.EndsWith(PartSuffix, StringComparison.Ordinal)))
            return null;

        // The folder above bin names the mod; an archive without one takes its name from the file.
        var binIndex = paths.Select(p => p.MenuConfigBinIndex).FirstOrDefault(i => i >= 0, -1);
        var root = binIndex > 0 ? string.Join('/', paths.First(p => p.MenuConfigBinIndex == binIndex).Segments[..binIndex]) + "/" : "";
        var modName = binIndex > 0 ? paths.First(p => p.MenuConfigBinIndex == binIndex).Segments[binIndex - 1] : archiveModName;

        var placements = new List<Placement>();
        var taken = new HashSet<GamePath>();
        void Place(string source, string target)
        {
            var gamePath = new GamePath(LocationId.Game, target);
            if (taken.Add(gamePath)) placements.Add(new Placement(source, gamePath));
        }

        // A mod that ships a backup of the vanilla menu file next to its own would otherwise win by archive order.
        var menuXmls = paths.Where(p => p.MenuConfigBinIndex >= 0 && p.Extension == ".xml").ToArray();
        foreach (var group in menuXmls.GroupBy(p => p.FileName, StringComparer.OrdinalIgnoreCase))
        {
            var winner = group.FirstOrDefault(p => !p.Lower.Contains("backup", StringComparison.Ordinal)) ?? group.First();
            Place(winner.Original, $"{MenuConfigFolder}/{winner.FileName}");
        }

        foreach (var path in paths.Where(p => p.Lower.StartsWith(root.ToLowerInvariant(), StringComparison.Ordinal) && !menuXmls.Contains(p)))
        {
            var relative = path.Original[root.Length..];
            if (relative.EndsWith(PartSuffix, StringComparison.OrdinalIgnoreCase))
            {
                // Not a game file: its keys are merged into the named INI, so it is kept under the tweak folder.
                var target = path.FileName[..^PartSuffix.Length];
                Place(path.Original, $"INI Tweaks/{modName} [{target}].ini");
            }
            else if (relative.StartsWith(Content + "/", StringComparison.OrdinalIgnoreCase))
                Place(path.Original, $"{WitcherConstants.W3ModsFolder}/{modName}/{relative}");
            else if (!Witcher3ModInstaller.DocumentationExtensions.Contains(new Extension(path.Extension)))
                Place(path.Original, relative);
        }

        return placements.ToArray();
    }

    private static Placement[]? Mixed(ArchivePath[] paths)
    {
        var tagged = paths.Select(p => (Path: p, Kind: p.FolderKind())).ToArray();
        var hasDlc = tagged.Any(t => t.Kind == ContentFolderKind.Dlc);
        var hasMod = tagged.Any(t => t.Kind == ContentFolderKind.Mod);
        if (!hasDlc || !hasMod) return null;

        return tagged
            .Where(t => t.Kind != ContentFolderKind.None)
            .Select(t => new Placement(t.Path.Original, Target(
                (t.Kind == ContentFolderKind.Dlc ? WitcherConstants.W3DlcFolder : WitcherConstants.W3ModsFolder)
                + "/" + string.Join('/', t.Path.Segments[(t.Path.ContentIndex - 1)..]))))
            .ToArray();
    }

    private static Placement[]? TopLevelMods(ArchivePath[] paths)
    {
        var inMods = paths.Where(p => p.ModsIndex >= 0).ToArray();
        if (inMods.Length == 0) return null;

        return inMods
            .Select(p => new Placement(p.Original, Target(WitcherConstants.W3ModsFolder + "/" + string.Join('/', p.Segments[(p.ModsIndex + 1)..]))))
            .ToArray();
    }

    private static Placement[]? BareContent(ArchivePath[] paths, string modName)
    {
        var content = paths.Where(p => p.Segments.Length > 1 && p.Segments[0].Equals(Content, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (content.Length == 0) return null;

        return content
            .Select(p => new Placement(p.Original, Target($"{WitcherConstants.W3ModsFolder}/{modName}/{p.Original}")))
            .ToArray();
    }

    private static Placement[]? Dlc(ArchivePath[] paths)
    {
        var dlc = paths.Where(p => p.Segments.Length > 1 && p.Segments[0].StartsWith("dlc", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (dlc.Length == 0) return null;

        // An archive already rooted at dlc/ keeps its dlc folders; one that starts at the dlc folder gets the root added.
        return dlc
            .Select(p => new Placement(p.Original, Target(p.Segments is [var first, _, ..] && first.Equals(WitcherConstants.W3DlcFolder, StringComparison.OrdinalIgnoreCase) && p.Segments.Length > 2
                ? WitcherConstants.W3DlcFolder + "/" + string.Join('/', p.Segments[1..])
                : WitcherConstants.W3DlcFolder + "/" + p.Original)))
            .ToArray();
    }

    private static GamePath Target(string path) => new(LocationId.Game, path);

    private sealed class ArchivePath(string original)
    {
        private static readonly string[] MenuConfigSegments = MenuConfigFolder.Split('/');

        public string Original { get; } = original.Replace('\\', '/');
        public string Lower { get; } = original.Replace('\\', '/').ToLowerInvariant();
        public string[] Segments { get; } = original.Replace('\\', '/').Split('/');
        public string FileName => Segments[^1];
        public string Extension => Path.GetExtension(FileName).ToLowerInvariant();

        /// <summary>Index of the <c>bin</c> segment that starts the menu config folder, or -1.</summary>
        public int MenuConfigBinIndex
        {
            get
            {
                for (var i = 0; i + MenuConfigSegments.Length < Segments.Length; i++)
                {
                    if (MenuConfigSegments.Select((s, j) => Segments[i + j].Equals(s, StringComparison.OrdinalIgnoreCase)).All(b => b))
                        return i;
                }
                return -1;
            }
        }

        /// <summary>Index of the first <c>mods</c> folder segment, or -1.</summary>
        public int ModsIndex => Array.FindIndex(Segments[..^1], s => s.Equals(WitcherConstants.W3ModsFolder, StringComparison.OrdinalIgnoreCase));

        /// <summary>Index of the first <c>content</c> folder segment that has a folder above it, or -1.</summary>
        public int ContentIndex
        {
            get
            {
                var index = Array.FindIndex(Segments[..^1], s => s.Equals(Content, StringComparison.OrdinalIgnoreCase));
                return index >= 1 ? index : -1;
            }
        }

        public ContentFolderKind FolderKind()
        {
            if (ContentIndex < 0) return ContentFolderKind.None;
            var folder = Segments[ContentIndex - 1];
            if (folder.StartsWith("dlc", StringComparison.OrdinalIgnoreCase)) return ContentFolderKind.Dlc;
            return folder.StartsWith("mod", StringComparison.OrdinalIgnoreCase) ? ContentFolderKind.Mod : ContentFolderKind.None;
        }
    }
}
