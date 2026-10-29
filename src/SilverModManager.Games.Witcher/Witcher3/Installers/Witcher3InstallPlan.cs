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
    private const string Bin = "bin";

    /// <summary>Extensions of the files a Witcher 3 mod ships inside its <c>content</c> folder.</summary>
    private static readonly HashSet<string> ContentExtensions = [".bundle", ".store", ".cache", ".ws", ".w3strings"];

    private static readonly string MenuConfigFolder = WitcherConstants.W3MenuConfigFolder.Path.Path;

    /// <returns>The placements of the first rule that claims the archive, or null when none does.</returns>
    public static IReadOnlyList<Placement>? Plan(IReadOnlyList<string> archivePaths, string archiveFileName)
    {
        var paths = archivePaths.Select(p => new ArchivePath(p)).ToArray();
        var modName = Witcher3ModInstaller.ModNameFromArchive(archiveFileName);

        return MenuMod(paths, modName)
               ?? TopLevelMods(paths)
               ?? Mixed(paths)
               ?? ModFolders(paths)
               ?? BareContent(paths, modName)
               ?? WrapperContent(paths)
               ?? Dlc(paths)
               ?? RootFiles(paths, modName);
    }

    private static Placement[]? MenuMod(ArchivePath[] paths, string archiveModName)
    {
        if (!paths.Any(p => p.Lower.Contains(MenuConfigFolder, StringComparison.Ordinal) || p.Lower.EndsWith(PartSuffix, StringComparison.Ordinal)))
            return null;

        // The folder above bin names the mod; an archive without one takes its name from the file.
        // Without a bin folder the folder holding the .part.txt plays that part, so content lands under Mods/<that folder>.
        var binIndex = paths.Select(p => p.MenuConfigBinIndex).FirstOrDefault(i => i >= 0, -1);
        var rootSegments = binIndex >= 0
            ? paths.First(p => p.MenuConfigBinIndex == binIndex).Segments[..binIndex]
            : paths.FirstOrDefault(p => p.Lower.EndsWith(PartSuffix, StringComparison.Ordinal))?.Segments[..^1] ?? [];
        var root = rootSegments.Length > 0 ? string.Join('/', rootSegments) + "/" : "";
        var modName = rootSegments.Length > 0 ? rootSegments[^1] : archiveModName;

        var placements = new List<Placement>();
        var taken = new HashSet<GamePath>();
        void Place(string source, string target)
        {
            var gamePath = new GamePath(LocationId.Game, target);
            if (taken.Add(gamePath)) placements.Add(new Placement(source, gamePath));
        }

        // A mod that ships a backup of the vanilla menu file next to its own would otherwise win by archive order.
        var menuXmls = MenuXmls(paths);
        foreach (var winner in MenuXmlWinners(menuXmls))
            Place(winner.Original, $"{MenuConfigFolder}/{winner.FileName}");

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
            else if (new ArchivePath(relative).LeadingContentFolderKind() is var kind and not ContentFolderKind.None)
                Place(path.Original, $"{(kind == ContentFolderKind.Dlc ? WitcherConstants.W3DlcFolder : WitcherConstants.W3ModsFolder)}/{relative}");
            else if (!Witcher3ModInstaller.DocumentationExtensions.Contains(new Extension(path.Extension)))
                Place(path.Original, relative);
        }

        return placements.ToArray();
    }

    private static ArchivePath[] MenuXmls(ArchivePath[] paths) =>
        paths.Where(p => p.MenuConfigBinIndex >= 0 && p.Extension == ".xml").ToArray();

    private static IEnumerable<ArchivePath> MenuXmlWinners(ArchivePath[] menuXmls) =>
        menuXmls.GroupBy(p => p.FileName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.FirstOrDefault(p => !p.Lower.Contains("backup", StringComparison.Ordinal)) ?? group.First());

    /// <summary>
    /// The copies of a menu XML that lose to another of the same name, normally the vanilla backup a mod ships
    /// beside its own file. They are left out on purpose, so nothing reports them as unplaced.
    /// </summary>
    public static IReadOnlySet<string> SkippedMenuXmlCopies(IReadOnlyList<string> archivePaths)
    {
        var menuXmls = MenuXmls(archivePaths.Select(p => new ArchivePath(p)).ToArray());
        var winners = MenuXmlWinners(menuXmls).ToHashSet();
        return menuXmls.Where(p => !winners.Contains(p)).Select(p => p.Original).ToHashSet();
    }

    /// <summary>Dlc and mod folders together: each goes to its own game folder, whatever wraps it in the archive.</summary>
    private static Placement[]? Mixed(ArchivePath[] paths)
    {
        var folders = ContentFolders(paths);
        if (!folders.Any(f => f.Kind == ContentFolderKind.Dlc) || !folders.Any(f => f.Kind == ContentFolderKind.Mod)) return null;
        return PlaceInFolders(paths, folders);
    }

    /// <summary>One or more <c>mod*</c> folders holding a <c>content</c> folder, at the root or under wrappers.</summary>
    private static Placement[]? ModFolders(ArchivePath[] paths)
    {
        var folders = ContentFolders(paths).Where(f => f.Kind == ContentFolderKind.Mod).ToArray();
        return folders.Length == 0 ? null : PlaceInFolders(paths, folders);
    }

    /// <summary>
    /// The folders that hold a <c>content</c> folder and are named <c>dlc*</c> or <c>mod*</c>, one entry per folder.
    /// </summary>
    private static ContentFolder[] ContentFolders(ArchivePath[] paths) =>
        paths.Select(p => (Path: p, Kind: p.FolderKind()))
            .Where(t => t.Kind != ContentFolderKind.None)
            .Select(t => new ContentFolder(t.Path.Segments[..t.Path.ContentIndex], t.Kind))
            .DistinctBy(f => string.Join('/', f.Segments), StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// Moves every folder to <c>dlc/</c> or <c>Mods/</c> with the wrappers above it dropped. Files beside the
    /// <c>content</c> folder inside it (a witcherscript.toml, say) go along; documentation does not. Anything
    /// outside the folders is left for the unplaced-files report.
    /// </summary>
    private static Placement[]? PlaceInFolders(ArchivePath[] paths, ContentFolder[] folders)
    {
        var placements = new List<Placement>();
        foreach (var path in paths)
        {
            var folder = folders.FirstOrDefault(f => path.Segments.Length > f.Segments.Length && path.StartsWith(f.Segments));
            if (folder is null) continue;
            var insideContent = path.Segments[folder.Segments.Length].Equals(Content, StringComparison.OrdinalIgnoreCase);
            if (!insideContent && path.IsDocumentation) continue;

            // A bin folder beside content is a script extender or config set, not part of the mod folder.
            if (!insideContent && path.Segments[folder.Segments.Length].Equals(Bin, StringComparison.OrdinalIgnoreCase))
            {
                placements.Add(new Placement(path.Original, Target(Bin + "/" + string.Join('/', path.Segments[(folder.Segments.Length + 1)..]))));
                continue;
            }

            var root = folder.Kind == ContentFolderKind.Dlc ? WitcherConstants.W3DlcFolder : WitcherConstants.W3ModsFolder;
            placements.Add(new Placement(path.Original, Target(root + "/" + string.Join('/', path.Segments[(folder.Segments.Length - 1)..]))));
        }
        return WithoutCollisions(placements);
    }

    /// <summary>
    /// Null when two files land on the same target (case-insensitive). Dropping a wrapper folder can fold two
    /// variants of a mod onto each other, and which one would be deployed is undefined, so the user picks by hand.
    /// </summary>
    private static Placement[]? WithoutCollisions(List<Placement> placements)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return placements.All(p => seen.Add(p.Target.ToString())) ? placements.ToArray() : null;
    }

    private sealed record ContentFolder(string[] Segments, ContentFolderKind Kind);

    /// <summary>
    /// Everything under a <c>Mods</c> folder, wherever the folder sits. The <c>bin</c> folder and the <c>dlc</c>
    /// or <c>dlc*</c> folders beside it go to the game's own folders, and so do <c>mod*</c> and <c>dlc*</c>
    /// content folders found outside the Mods folder.
    /// </summary>
    private static Placement[]? TopLevelMods(ArchivePath[] paths)
    {
        var first = paths.FirstOrDefault(p => p.ModsIndex >= 0);
        if (first is null) return null;
        var wrapper = first.Segments[..first.ModsIndex];

        var placements = new List<Placement>();
        var placed = new HashSet<string>();
        foreach (var path in paths.Where(p => p.Segments.Length > wrapper.Length + 1 && p.StartsWith(wrapper)))
        {
            var rest = path.Segments[wrapper.Length..];
            string? target = null;
            if (rest[0].Equals(WitcherConstants.W3ModsFolder, StringComparison.OrdinalIgnoreCase))
                target = WitcherConstants.W3ModsFolder + "/" + string.Join('/', rest[1..]);
            else if (path.IsDocumentation)
                continue;
            else if (rest[0].Equals(Bin, StringComparison.OrdinalIgnoreCase))
                target = Bin + "/" + string.Join('/', rest[1..]);
            else if (rest[0].Equals(WitcherConstants.W3DlcFolder, StringComparison.OrdinalIgnoreCase))
                target = WitcherConstants.W3DlcFolder + "/" + string.Join('/', rest[1..]);
            else if (rest[0].StartsWith("dlc", StringComparison.OrdinalIgnoreCase) && rest.Length > 2)
                target = WitcherConstants.W3DlcFolder + "/" + string.Join('/', rest);

            if (target is null) continue;
            placements.Add(new Placement(path.Original, Target(target)));
            placed.Add(path.Original);
        }

        var others = paths.Where(p => !placed.Contains(p.Original)).ToArray();
        if (PlaceInFolders(others, ContentFolders(others)) is { } extra) placements.AddRange(extra);
        else return null;
        return WithoutCollisions(placements);
    }

    private static Placement[]? BareContent(ArchivePath[] paths, string modName)
    {
        var content = paths.Where(p => p.Segments.Length > 1 && p.Segments[0].Equals(Content, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (content.Length == 0) return null;

        return content
            .Select(p => new Placement(p.Original, Target($"{WitcherConstants.W3ModsFolder}/{modName}/{p.Original}")))
            .ToArray();
    }

    /// <summary>
    /// A single folder over <c>content</c> that is not named for the game's loader. The game only loads Mods
    /// folders whose names start with <c>mod</c>, so the wrapper's name gets that prefix.
    /// </summary>
    private static Placement[]? WrapperContent(ArchivePath[] paths)
    {
        var wrappers = paths.Where(p => p.ContentIndex == 1 && p.FolderKind() == ContentFolderKind.None).Select(p => p.Segments[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // Several wrappers are variants of one mod (Option A, Option B); the user has to pick.
        if (wrappers.Length != 1) return null;

        var placements = new List<Placement>();
        foreach (var path in paths.Where(p => p.Segments.Length > 1 && p.Segments[0].Equals(wrappers[0], StringComparison.OrdinalIgnoreCase)))
        {
            var insideContent = path.Segments[1].Equals(Content, StringComparison.OrdinalIgnoreCase);
            if (insideContent && path.Segments.Length > 2)
                placements.Add(new Placement(path.Original, Target($"{WitcherConstants.W3ModsFolder}/{Witcher3ModInstaller.ModFolderName(path.Segments[0])}/{string.Join('/', path.Segments[1..])}")));
            else if (path.IsDocumentation) continue;
            else if (path.Segments.Length > 2 && path.Segments[1].Equals(Bin, StringComparison.OrdinalIgnoreCase))
                placements.Add(new Placement(path.Original, Target(Bin + "/" + string.Join('/', path.Segments[2..]))));
            else if (!insideContent)
                placements.Add(new Placement(path.Original, Target($"{WitcherConstants.W3ModsFolder}/{Witcher3ModInstaller.ModFolderName(path.Segments[0])}/{string.Join('/', path.Segments[1..])}")));
        }
        return placements.ToArray();
    }

    /// <summary>
    /// Dlc folders at any depth: below a <c>dlc</c> folder, or a <c>dlc*</c> folder holding <c>content</c>. A
    /// <c>dlc*</c> folder at the archive root is placed whatever it holds.
    /// </summary>
    private static Placement[]? Dlc(ArchivePath[] paths)
    {
        var placements = new List<Placement>();
        foreach (var path in paths)
        {
            var dlcIndex = Array.FindIndex(path.Segments[..^1], s => s.Equals(WitcherConstants.W3DlcFolder, StringComparison.OrdinalIgnoreCase));
            string[]? rest = null;
            if (dlcIndex >= 0 && path.Segments.Length > dlcIndex + 1)
                rest = path.Segments[(dlcIndex + 1)..];
            else if (path.FolderKind() == ContentFolderKind.Dlc)
                rest = path.Segments[(path.ContentIndex - 1)..];
            else if (path.Segments.Length > 1 && path.Segments[0].StartsWith("dlc", StringComparison.OrdinalIgnoreCase))
                rest = path.Segments;

            if (rest is not null && (!path.IsDocumentation || path.ContentIndex >= 0))
                placements.Add(new Placement(path.Original, Target(WitcherConstants.W3DlcFolder + "/" + string.Join('/', rest))));
        }
        return placements.Count == 0 ? null : placements.ToArray();
    }

    /// <summary>
    /// Files that belong at the game root or in a fresh mod folder: a <c>bin</c> folder (script extender
    /// DLLs, ini files) goes to the game's bin, and loose content files (bundles, caches, scripts) go under
    /// <c>Mods/mod&lt;archive&gt;/content</c>. One wrapper folder over all of it is dropped.
    /// </summary>
    private static Placement[]? RootFiles(ArchivePath[] paths, string modName)
    {
        var skip = SharedWrapperDepth(paths);
        var placements = new List<Placement>();
        foreach (var path in paths)
        {
            var segments = path.Segments[skip..];
            if (segments.Length == 0) continue;
            if (segments.Length > 1 && segments[0].Equals(Bin, StringComparison.OrdinalIgnoreCase))
            {
                if (!path.IsDocumentation)
                    placements.Add(new Placement(path.Original, Target(Bin + "/" + string.Join('/', segments[1..]))));
            }
            else if (segments.Length == 1 && ContentExtensions.Contains(path.Extension))
                placements.Add(new Placement(path.Original, Target($"{WitcherConstants.W3ModsFolder}/{modName}/{Content}/{segments[0]}")));
        }
        return placements.Count == 0 ? null : placements.ToArray();
    }

    /// <summary>1 when every file that is not documentation sits inside the same folder and that folder is not <c>bin</c>, else 0.</summary>
    private static int SharedWrapperDepth(ArchivePath[] paths)
    {
        var files = paths.Where(p => !p.IsDocumentation).ToArray();
        return files.Length > 0
               && !files[0].Segments[0].Equals(Bin, StringComparison.OrdinalIgnoreCase)
               && files.All(p => p.Segments.Length > 1 && p.Segments[0].Equals(files[0].Segments[0], StringComparison.OrdinalIgnoreCase))
            ? 1
            : 0;
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

        public bool IsDocumentation => Witcher3ModInstaller.DocumentationExtensions.Contains(new Extension(Extension));

        public bool StartsWith(string[] prefix) =>
            Segments.Length >= prefix.Length && prefix.Select((s, i) => Segments[i].Equals(s, StringComparison.OrdinalIgnoreCase)).All(b => b);

        /// <summary>The kind of the first folder when it is a <c>dlc*</c> or <c>mod*</c> folder with a <c>content</c> child, else none.</summary>
        public ContentFolderKind LeadingContentFolderKind() => ContentIndex == 1 ? FolderKind() : ContentFolderKind.None;

        public ContentFolderKind FolderKind()
        {
            if (ContentIndex < 0) return ContentFolderKind.None;
            var folder = Segments[ContentIndex - 1];
            if (folder.StartsWith("dlc", StringComparison.OrdinalIgnoreCase)) return ContentFolderKind.Dlc;
            return folder.StartsWith("mod", StringComparison.OrdinalIgnoreCase) ? ContentFolderKind.Mod : ContentFolderKind.None;
        }
    }
}
