using NexusMods.Paths;

namespace NexusMods.Deployment.Vfs;

/// <summary>
/// What is staged and how big it is, from file lengths alone: nothing is read or hashed, so a
/// report on tens of gigabytes costs a directory walk.
/// </summary>
/// <param name="Files">Staged files, largest first; equal sizes in path order.</param>
public sealed record StagingReport(IReadOnlyList<StagedFile> Files, Size Total)
{
    public static StagingReport Build(StagingDirectory staging)
    {
        var files = staging.Scan()
            .Where(e => !e.IsDirectory)
            .Select(e => new StagedFile(e.Path, e.Size))
            .OrderByDescending(f => f.Size.Value)
            .ThenBy(f => f.Path.ToString(), StringComparer.Ordinal)
            .ToArray();
        return new StagingReport(files, Size.From((ulong)files.Sum(f => (decimal)f.Size.Value)));
    }
}

/// <summary>A staged file at its mount-relative path.</summary>
public readonly record struct StagedFile(RelativePath Path, Size Size);
