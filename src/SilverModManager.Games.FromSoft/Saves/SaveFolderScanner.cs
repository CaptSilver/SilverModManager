namespace SilverModManager.Games.FromSoft.Saves;

/// <summary>What a file in a save folder is, going by its name.</summary>
public enum SaveKind
{
    /// <summary>The save the game writes (<see cref="FromSoftSaveNames.Vanilla"/>).</summary>
    Vanilla,

    /// <summary>The save Seamless Co-op writes (<see cref="FromSoftSaveNames.SeamlessCoop"/>).</summary>
    SeamlessCoop,

    /// <summary>The save a modded session without Seamless Co-op is pointed at (<see cref="FromSoftSaveNames.Modded"/>).</summary>
    Modded,

    /// <summary>Anything else. Listed so the user sees it, never read, moved, backed up or restored.</summary>
    Unknown,
}

/// <param name="Name">The file name.</param>
/// <param name="Path">The full path.</param>
/// <param name="Kind">What the name says the file is.</param>
/// <param name="IsBackup">True for the game's own <c>.bak</c> copy of a known save.</param>
public sealed record SaveFileEntry(string Name, string Path, SaveKind Kind, bool IsBackup, long Size, DateTimeOffset Modified)
{
    /// <summary>Whether the page may act on the file at all.</summary>
    public bool IsKnown => Kind != SaveKind.Unknown;
}

/// <summary>
/// Lists a save folder and says which files the game's <see cref="FromSoftSaveNames"/> know. Every operation on a
/// save asks <see cref="KindOf"/> first, so a file with a name nobody here wrote (an <c>ER0000_modded</c> from an
/// earlier tool, say) is shown and left alone. A game without a modded or Seamless Co-op save never yields that kind.
/// </summary>
public sealed class SaveFolderScanner
{
    private const string BackupSuffix = ".bak";

    /// <summary>The known save files, with the kind each name stands for. Names are matched exactly, as the game writes them.</summary>
    private readonly (string Name, SaveKind Kind)[] _knownNames;

    public SaveFolderScanner(FromSoftSaveNames names)
    {
        var known = new List<(string, SaveKind)> { (names.Vanilla, SaveKind.Vanilla) };
        if (names.SeamlessCoop is { } coop) known.Add((coop, SaveKind.SeamlessCoop));
        if (names.Modded is { } modded) known.Add((modded, SaveKind.Modded));
        _knownNames = known.ToArray();
    }

    public SaveKind KindOf(string fileName, out bool isBackup)
    {
        foreach (var (name, kind) in _knownNames)
        {
            if (fileName == name) { isBackup = false; return kind; }
            if (fileName == name + BackupSuffix) { isBackup = true; return kind; }
        }

        isBackup = false;
        return SaveKind.Unknown;
    }

    /// <summary>The files of the folder, known saves first and then by name. A folder that does not exist has none.</summary>
    public IReadOnlyList<SaveFileEntry> Scan(string folder)
    {
        if (!Directory.Exists(folder)) return [];

        return new DirectoryInfo(folder).EnumerateFiles()
            .Select(file =>
            {
                var kind = KindOf(file.Name, out var isBackup);
                return new SaveFileEntry(file.Name, file.FullName, kind, isBackup, file.Length, file.LastWriteTimeUtc);
            })
            .OrderBy(e => e.Kind == SaveKind.Unknown)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .ToArray();
    }
}
