using System.IO.Compression;

namespace SilverModManager.App.UI.Windows;

public enum DropKind
{
    /// <summary>A <c>.wabbajack</c> file: added to the library, then its page opens.</summary>
    Modlist,

    /// <summary>A mod archive: added to the library.</summary>
    Mod,

    /// <summary>Nothing happens to it; <see cref="DropItem.Reason"/> says why.</summary>
    Refused,
}

public sealed record DropItem(string Path, DropKind Kind, string? Reason = null)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// What a set of files dropped on the main window turns into. Pure classification: nothing is added or opened here,
/// so the overlay can show the outcome while the files are still being dragged.
/// </summary>
public sealed class MainWindowDropPlan
{
    private const string CollectionManifest = "collection.json";
    private static readonly string[] ArchiveExtensions = [".zip", ".7z", ".rar"];

    private MainWindowDropPlan(IReadOnlyList<DropItem> items) => Items = items;

    public IReadOnlyList<DropItem> Items { get; }
    public IEnumerable<DropItem> Modlists => Items.Where(static item => item.Kind == DropKind.Modlist);
    public IEnumerable<DropItem> Mods => Items.Where(static item => item.Kind == DropKind.Mod);
    public IEnumerable<DropItem> Refused => Items.Where(static item => item.Kind == DropKind.Refused);

    public static MainWindowDropPlan Classify(IEnumerable<string> paths)
        => new(paths.Distinct(StringComparer.Ordinal).Select(ClassifyOne).ToArray());

    private static DropItem ClassifyOne(string path)
    {
        var name = Path.GetFileName(path);
        if (Directory.Exists(path)) return Refuse(path, "it is a folder; drop the archives inside it instead");
        if (!File.Exists(path)) return Refuse(path, "the file does not exist");
        if (!CanRead(path)) return Refuse(path, "it can't be read");

        if (name.Equals(CollectionManifest, StringComparison.OrdinalIgnoreCase)) return Refuse(path, CollectionReason);

        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".wabbajack") return new DropItem(path, DropKind.Modlist);
        if (!ArchiveExtensions.Contains(extension)) return Refuse(path, "it is not a mod archive (.zip, .7z or .rar) or a .wabbajack modlist");
        if (extension == ".zip" && HoldsCollectionManifest(path)) return Refuse(path, CollectionReason);

        return new DropItem(path, DropKind.Mod);
    }

    // The app learns about a collection from Nexus, not from a file: its revision and download list come from the API.
    private const string CollectionReason = "it is a Nexus collection; open collections from the Collections tab of the Nexus Mods page";

    private static DropItem Refuse(string path, string reason) => new(path, DropKind.Refused, reason);

    private static bool CanRead(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // A zip that does not open is a mod archive the extractor will report on; it is not a collection.
    private static bool HoldsCollectionManifest(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            return archive.Entries.Any(static entry => entry.FullName.Equals(CollectionManifest, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The line the overlay shows while the files are over the window.</summary>
    public string OverlayText
    {
        get
        {
            var modlists = Modlists.ToArray();
            var mods = Mods.Count();
            var refused = Refused.ToArray();

            var parts = new List<string>();
            if (modlists.Length == 1) parts.Add($"Open modlist {Path.GetFileNameWithoutExtension(modlists[0].Path)}");
            else if (modlists.Length > 1) parts.Add($"Open {modlists.Length} modlists");
            if (mods > 0) parts.Add($"{(parts.Count == 0 ? "Add" : "add")} {ModCount(mods)} to the library");

            if (parts.Count == 0)
                return refused.Length == 1 ? $"Can't add {refused[0].FileName}" : $"Can't add {refused.Length} files";

            var text = string.Join(", ", parts);
            return refused.Length == 0 ? text : $"{text} (skipping {refused.Length} {(refused.Length == 1 ? "file" : "files")})";
        }
    }

    internal static string ModCount(int count) => count == 1 ? "1 mod" : $"{count} mods";
}
