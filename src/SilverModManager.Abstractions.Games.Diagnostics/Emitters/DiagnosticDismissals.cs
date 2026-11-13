using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Diagnostics.Emitters;

/// <summary>
/// What a loadout has told the Health Check to stop reporting, read by the emitter that would report it.
/// </summary>
public static class DiagnosticDismissals
{
    /// <summary>One ignored subject, with the content it had when it was ignored (null when the finding has none).</summary>
    public sealed record Dismissed(string Subject, string? Identity);

    /// <summary>
    /// The subjects <paramref name="loadoutId"/> ignores for the diagnostic <paramref name="id"/>. Read once per run,
    /// not once per file.
    /// </summary>
    public static IReadOnlyList<Dismissed> For(IDb db, LoadoutId loadoutId, DiagnosticId id)
        => DiagnosticDismissal.FindByLoadout(db, loadoutId)
            .Where(row => row.Source == id.Source && row.Number == id.Number)
            .OrderBy(row => row.Id.Value)
            .Select(row => new Dismissed(row.Subject, row.Identity.HasValue ? row.Identity.Value : null))
            .ToArray();

    /// <summary>
    /// Whether <paramref name="subject"/> as it is now (<paramref name="current"/>) is one the loadout ignores: the
    /// same subject, and when an identity was recorded, the same bytes. New bytes at an ignored path are reported again.
    /// </summary>
    public static bool Covers(IReadOnlyList<Dismissed> dismissed, string subject, FileIdentity current)
    {
        var text = IdentityText(current);
        return dismissed.Any(d => d.Subject == subject && (d.Identity is null || d.Identity == text));
    }

    /// <summary>The subject a file is dismissed under: its location and relative path, <c>Game|Data/SKSE/Plugins/x.dll</c>.</summary>
    public static string SubjectOf(GamePath path) => $"{path.LocationId}|{path.Path}";

    /// <summary>The form an identity takes in a dismissal: <c>XxHash3:&lt;hex digest&gt;</c>.</summary>
    public static string IdentityText(FileIdentity identity) => $"{identity.Kind}:{Convert.ToHexString(identity.Digest)}";
}
