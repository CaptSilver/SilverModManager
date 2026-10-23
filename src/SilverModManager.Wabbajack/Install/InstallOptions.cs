using NexusMods.Paths;
using SilverModManager.Wabbajack.Acquire;

namespace SilverModManager.Wabbajack.Install;

/// <summary>What the caller chose before starting an install.</summary>
/// <param name="SkipMissingArchives">Install what can be installed when archives are missing, and leave a skip row for each file that needed one.</param>
/// <param name="GameFilesNotToReplace">Files of the game folder (relative to it) the caller wants to keep as they are, whatever the list says.</param>
public sealed record InstallOptions(bool SkipMissingArchives, IReadOnlySet<RelativePath> GameFilesNotToReplace);

/// <summary>Thrown before anything is written when archives are missing and the caller did not choose to go on without them.</summary>
public sealed class ArchivesMissingException(IReadOnlyList<ArchivePlanRow> rows)
    : Exception($"{rows.Count} archive(s) of the list are not available: {string.Join(", ", rows.Take(5).Select(r => r.Archive.Name))}{(rows.Count > 5 ? ", ..." : "")}")
{
    /// <summary>The rows of the plan that are neither in the library nor a game file this install matches.</summary>
    public IReadOnlyList<ArchivePlanRow> Rows { get; } = rows;
}
