namespace NexusMods.Networking.ModUpdates;

/// <summary>
/// A file on a Nexus mod page, as far as choosing a pin needs to know it.
/// </summary>
public readonly record struct PinFile(ulong FileId, string FileName, string? Version);

/// <summary>
/// What to do with a pinned file.
/// </summary>
public enum PinAction
{
    /// <summary>The pin stays where it is.</summary>
    Unchanged,

    /// <summary>The pin moves to <see cref="PinChoice.Candidate"/>.</summary>
    Repin,

    /// <summary>More than one candidate fits, so the pin stays and <see cref="PinChoice.Reason"/> names them.</summary>
    Ambiguous,
}

/// <summary>
/// The outcome of <see cref="PinResolver.Resolve"/>. <see cref="Candidate"/> is set only for <see cref="PinAction.Repin"/>.
/// </summary>
public readonly record struct PinChoice(PinAction Action, PinFile? Candidate, string Reason);

/// <summary>
/// Decides whether a pinned Nexus file should move to a newer upload. Anything uncertain keeps the pin: the
/// worst outcome is an update the user is told about and has to take by hand, never a different variant
/// substituted for the one they chose.
/// </summary>
public static class PinResolver
{
    /// <summary>
    /// Picks what a pin on <paramref name="current"/> should do given the files offered in its place.
    /// </summary>
    /// <param name="current">The pinned file.</param>
    /// <param name="candidates">The newer files the page offers; the caller decides what counts as newer.</param>
    /// <param name="frozen">The pin must not move, whatever the page offers.</param>
    /// <param name="modId">The mod page's id, if known, to keep it out of the variant keys.</param>
    public static PinChoice Resolve(PinFile current, IReadOnlyCollection<PinFile> candidates, bool frozen, ulong? modId = null)
    {
        if (frozen) return new PinChoice(PinAction.Unchanged, Candidate: null, "frozen");
        if (candidates.Count == 0) return new PinChoice(PinAction.Unchanged, Candidate: null, "no newer file");

        var key = VariantKey.Of(current.FileName, current.Version, modId);
        var matches = candidates
            .Where(candidate => VariantKey.Of(candidate.FileName, candidate.Version, modId) == key)
            .ToArray();

        return matches.Length switch
        {
            1 => new PinChoice(PinAction.Repin, matches[0], $"variant '{key}'"),
            0 => new PinChoice(PinAction.Unchanged, Candidate: null, $"variant '{key}' matched 0 of {candidates.Count} candidates"),
            _ => new PinChoice(PinAction.Ambiguous, Candidate: null,
                $"variant '{key}' matched {matches.Length} of {candidates.Count} candidates: {string.Join(", ", matches.Select(m => m.FileId))}"),
        };
    }
}
