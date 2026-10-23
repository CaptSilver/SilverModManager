using System.IO.Enumeration;
using System.Text.Json;
using NexusMods.Hashing.xxHash3.Paths;
using NexusMods.Paths;

namespace SilverModManager.Games.FromSoft.Overlay;

/// <summary>What is wrong with a path the audit found.</summary>
public enum AuditFindingKind
{
    /// <summary>A file in the upper folder that is neither something the app wrote nor a known runtime file.</summary>
    Unexpected,

    /// <summary>A file in the upper folder at a path the real game folder has too: it replaces or hides a game file.</summary>
    ShadowedGameFile,

    /// <summary>A path of the real game folder that differs from before the session, so the overlay did not hold.</summary>
    GameFolderChanged,
}

/// <param name="Path">Relative to the game folder.</param>
/// <param name="Detail">For a changed game folder, what happened to the path (added, removed, changed); otherwise empty.</param>
public sealed record AuditFinding(AuditFindingKind Kind, string Path, string Detail = "");

/// <summary>
/// Looks through what a session left in the upper folder. A finding is kept on disk until it is discarded, so a
/// launch that follows an unresolved one can refuse, and the doctor can show it without a session running.
/// </summary>
public static class UpperdirAudit
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>
    /// Audits the upper folder and keeps what it finds. In order: a path the preparation wrote whose bytes are
    /// unchanged is fine; a path the real folder has too is a shadowed game file, allowed name or not; then the
    /// allowlist; anything else is a finding. The first rule is what keeps the configuration layer from flagging
    /// itself while the real folder still holds the same files. Findings about the game folder from an earlier
    /// audit are kept.
    /// </summary>
    public static async Task<IReadOnlyList<AuditFinding>> Run(OverlayLayout layout, OverlayAllowlist allowlist, CancellationToken cancellationToken = default)
    {
        var written = OverlaySessionState.Load(layout)?.Written ?? new Dictionary<string, string>();
        var upper = layout.Upper.GetFullPath();
        var findings = new List<AuditFinding>();

        if (layout.Upper.DirectoryExists())
        {
            foreach (var file in Directory.EnumerateFiles(upper, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(upper, file).Replace('\\', '/');

                if (written.TryGetValue(relative, out var expected) && await HashOf(layout, file, cancellationToken) == expected) continue;

                // A whiteout (a deleted game file) is a device node and shows up here as a file too.
                if (Path.Exists(Path.Combine(layout.Lower.GetFullPath(), relative)))
                {
                    findings.Add(new AuditFinding(AuditFindingKind.ShadowedGameFile, relative));
                    continue;
                }

                if (IsAllowed(allowlist, relative)) continue;
                findings.Add(new AuditFinding(AuditFindingKind.Unexpected, relative));
            }
        }

        findings.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        var kept = PendingFindings(layout).Where(f => f.Kind == AuditFindingKind.GameFolderChanged);
        Save(layout, [..findings, ..kept]);
        return findings;
    }

    /// <summary>Whether a path under the upper folder is a runtime file the game or a mod is expected to write.</summary>
    public static bool IsAllowed(OverlayAllowlist allowlist, string relativePath)
    {
        if (allowlist.Folders.Any(folder => relativePath.StartsWith(folder, StringComparison.OrdinalIgnoreCase))) return true;

        var name = relativePath[(relativePath.LastIndexOf('/') + 1)..];
        return allowlist.Names.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true));
    }

    /// <summary>Records paths of the real game folder that changed during a session, next to the upper folder's findings.</summary>
    public static void RecordGameFolderChanges(OverlayLayout layout, IReadOnlyList<GameFolderSnapshot.Difference> differences)
    {
        var upperFindings = PendingFindings(layout).Where(f => f.Kind != AuditFindingKind.GameFolderChanged);
        Save(layout, [..upperFindings, ..differences.Select(d => new AuditFinding(AuditFindingKind.GameFolderChanged, d.Path, d.Change))]);
    }

    /// <summary>What the last audit found and nobody has discarded. Empty when there is nothing.</summary>
    public static IReadOnlyList<AuditFinding> PendingFindings(OverlayLayout layout)
    {
        if (!layout.Findings.FileExists) return [];
        return JsonSerializer.Deserialize<List<AuditFinding>>(File.ReadAllText(layout.Findings.GetFullPath()), Json) ?? [];
    }

    /// <summary>
    /// Resolves a finding. A file of the upper folder is deleted there and nowhere else: the real game folder is
    /// never touched. A finding about the game folder has nothing in the upper folder to delete, so discarding it
    /// only says the user has looked.
    /// </summary>
    public static void Discard(OverlayLayout layout, AuditFinding finding)
    {
        if (finding.Kind != AuditFindingKind.GameFolderChanged)
        {
            var upper = layout.Upper.GetFullPath();
            var target = Path.GetFullPath(Path.Combine(upper, finding.Path));
            if (!target.StartsWith(upper + "/", StringComparison.Ordinal))
                throw new InvalidOperationException($"'{finding.Path}' is not inside the overlay's upper folder, so it is not discarded.");
            if (Path.Exists(target)) File.Delete(target);
        }

        Save(layout, PendingFindings(layout).Where(f => f != finding).ToList());
    }

    internal static async Task<string> HashOf(OverlayLayout layout, string file, CancellationToken cancellationToken)
        => (await layout.Root.FileSystem.FromUnsanitizedFullPath(file).XxHash3Async(token: cancellationToken)).Value.ToString("x16");

    private static void Save(OverlayLayout layout, IReadOnlyList<AuditFinding> findings)
    {
        layout.Root.CreateDirectory();
        File.WriteAllText(layout.Findings.GetFullPath(), JsonSerializer.Serialize(findings, Json));
    }
}

/// <summary>
/// What a preparation recorded for the audit that follows the session: the files it wrote with their hashes, and
/// the game folder as it was. Kept in a file because the session may outlive the process that prepared it.
/// </summary>
internal sealed record OverlaySessionState(Dictionary<string, string> Written, GameFolderSnapshot Snapshot)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static OverlaySessionState? Load(OverlayLayout layout)
        => layout.SessionState.FileExists ? JsonSerializer.Deserialize<OverlaySessionState>(File.ReadAllText(layout.SessionState.GetFullPath()), Json) : null;

    public void Save(OverlayLayout layout)
    {
        layout.Root.CreateDirectory();
        File.WriteAllText(layout.SessionState.GetFullPath(), JsonSerializer.Serialize(this, Json));
    }
}
