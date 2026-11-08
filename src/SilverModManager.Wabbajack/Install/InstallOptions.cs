using NexusMods.Paths;
using SilverModManager.Sdk.Errors;
using SilverModManager.Wabbajack.Acquire;

namespace SilverModManager.Wabbajack.Install;

/// <summary>What the caller chose before starting an install.</summary>
/// <param name="SkipMissingArchives">Install what can be installed when archives are missing, and leave a skip row for each file that needed one.</param>
/// <param name="GameFilesNotToReplace">Files of the game folder (relative to it) the caller wants to keep as they are, whatever the list says.</param>
public sealed record InstallOptions(bool SkipMissingArchives, IReadOnlySet<RelativePath> GameFilesNotToReplace);

/// <summary>An archive plan with the rows of it the install cannot go on without.</summary>
/// <param name="Plan">The archives the list names and what the library and the game hold of each.</param>
/// <param name="Missing">The rows that are neither in the library nor a game file this install matches.</param>
public sealed record ArchiveCheck(ArchivePlan Plan, IReadOnlyList<ArchivePlanRow> Missing)
{
    /// <summary>The check of <paramref name="plan"/>. A game file of another version is not a download to fetch: its directives are skipped with that reason instead.</summary>
    public static ArchiveCheck Of(ArchivePlan plan)
        => new(plan, plan.Rows.Where(r => r.State is not (ArchiveState.Have or ArchiveState.GameFile or ArchiveState.GameFileVersion or ArchiveState.NotNeeded)).ToArray());
}

/// <summary>Thrown before anything is written when archives are missing and the caller did not choose to go on without them.</summary>
public sealed class ArchivesMissingException(IReadOnlyList<ArchivePlanRow> rows)
    : UserFacingException($"{rows.Count} archive(s) of the list are not available: {string.Join(", ", rows.Take(5).Select(r => MarkdownText.Code(r.Archive.Name)))}{(rows.Count > 5 ? ", ..." : "")}. " +
                          "Add them to the library, or tick \"Skip files from missing archives\" to install without them.")
{
    /// <inheritdoc />
    public override string Title => "The modlist was not installed";

    /// <summary>The rows of the plan that are neither in the library nor a game file this install matches.</summary>
    public IReadOnlyList<ArchivePlanRow> Rows { get; } = rows;
}
