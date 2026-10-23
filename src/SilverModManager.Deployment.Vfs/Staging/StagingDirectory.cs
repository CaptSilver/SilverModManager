using NexusMods.Sdk.Games;
using NexusMods.Paths;
using System.Globalization;

namespace NexusMods.Deployment.Vfs;

/// <summary>
/// Where one installation's mounted folder keeps what the game changed. Files sit at their
/// mount-relative path inside <see cref="Root"/>; the names the game deleted or renamed away, which
/// live in the backing directory or the loadout and so cannot be removed, are listed in
/// <see cref="WhiteoutFile"/>. That list is a sibling of <see cref="Root"/>, not a file inside it,
/// because everything inside the root is a game path and a game file called <c>.whiteouts</c> at the
/// mount root would collide with it.
/// </summary>
public sealed class StagingDirectory
{
    private const string WhiteoutSuffix = ".whiteouts";
    private const string JournalSuffix = ".promotion.json";
    private const string RecoverySuffix = ".recovery";

    /// <param name="stagingLocation">The staging location setting; one subdirectory per installation lives under it, named like the loose staging store's.</param>
    public StagingDirectory(AbsolutePath stagingLocation, GameInstallMetadataId installation)
    {
        var name = installation.Value.Value.ToString("x", CultureInfo.InvariantCulture);
        Installation = installation;
        Root = stagingLocation.Combine(name);
        JournalFile = stagingLocation.Combine(name + JournalSuffix);
        RecoveryRoot = stagingLocation.Combine(name + RecoverySuffix);
        WhiteoutFile = stagingLocation.Combine(name + WhiteoutSuffix);
        TempDirectory = stagingLocation.Combine(name + ".tmp");
    }

    /// <summary>The installation whose game wrote these files.</summary>
    public GameInstallMetadataId Installation { get; }

    /// <summary>
    /// Where an unfinished promotion is written down. A sibling of <see cref="Root"/>, so clearing
    /// the staging directory cannot take the journal with it.
    /// </summary>
    public AbsolutePath JournalFile { get; }

    /// <summary>
    /// Holds one timestamped folder per promotion that was refused because the staged files
    /// changed under it.
    /// </summary>
    public AbsolutePath RecoveryRoot { get; }

    /// <summary>Staged files, at their mount-relative paths.</summary>
    public AbsolutePath Root { get; }

    /// <summary>One mount-relative path per line, sorted ordinally.</summary>
    public AbsolutePath WhiteoutFile { get; }

    // Copies are built here and renamed into place, so a scan of Root never sees a half-written file.
    // A sibling of Root so the rename stays on one filesystem.
    private AbsolutePath TempDirectory { get; }

    public AbsolutePath PathFor(RelativePath path) => Root.Combine(path);

    public bool Contains(AbsolutePath path) => path.InFolder(Root);

    /// <summary>A path for a file that will be renamed into <see cref="Root"/> once complete.</summary>
    public AbsolutePath NewTempPath()
    {
        TempDirectory.CreateDirectory();
        return TempDirectory.Combine(Guid.NewGuid().ToString("N"));
    }

    public IReadOnlyList<RelativePath> ReadWhiteouts()
    {
        if (!WhiteoutFile.FileExists) return [];
        return File.ReadAllLines(WhiteoutFile.ToString())
            .Where(line => line.Length > 0)
            .Select(line => RelativePath.FromUnsanitizedInput(line))
            .ToArray();
    }

    /// <summary>
    /// Replaces the list. It is written next to the old one and renamed over it, so a crash leaves
    /// one complete list or the other.
    /// </summary>
    public void WriteWhiteouts(IEnumerable<RelativePath> paths)
    {
        var lines = paths.Select(p => p.ToString()).Order(StringComparer.Ordinal).ToArray();
        WhiteoutFile.Parent.CreateDirectory();

        var temp = WhiteoutFile.Parent.Combine(WhiteoutFile.FileName + ".tmp");
        File.WriteAllLines(temp.ToString(), lines);
        File.Move(temp.ToString(), WhiteoutFile.ToString(), overwrite: true);
    }

    /// <summary>Every file and directory staged so far, parents before their children.</summary>
    public IReadOnlyList<StagedEntry> Scan()
    {
        var entries = new List<StagedEntry>();
        if (Root.DirectoryExists()) Walk(Root, RelativePath.Empty, entries);
        return entries;
    }

    private static void Walk(AbsolutePath directory, RelativePath relative, List<StagedEntry> entries)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(directory.ToString()).Order(StringComparer.Ordinal))
        {
            var name = System.IO.Path.GetFileName(path);
            var child = directory.Combine(name);
            var childRelative = relative.Join(RelativePath.FromUnsanitizedInput(name));
            if (Directory.Exists(path))
            {
                entries.Add(new StagedEntry(childRelative, true, child, Size.Zero));
                Walk(child, childRelative, entries);
            }
            else
            {
                entries.Add(new StagedEntry(childRelative, false, child, Size.From((ulong)new FileInfo(path).Length)));
            }
        }
    }

    /// <summary>
    /// Forgets everything the game wrote: the staged files, the whiteout list and any unfinished copies.
    /// </summary>
    public void Clear()
    {
        if (Root.DirectoryExists()) Root.DeleteDirectory(recursive: true);
        if (TempDirectory.DirectoryExists()) TempDirectory.DeleteDirectory(recursive: true);
        if (WhiteoutFile.FileExists) WhiteoutFile.Delete();
    }
}

/// <summary>One file or directory found under a <see cref="StagingDirectory"/>.</summary>
public readonly record struct StagedEntry(RelativePath Path, bool IsDirectory, AbsolutePath RealPath, Size Size);
