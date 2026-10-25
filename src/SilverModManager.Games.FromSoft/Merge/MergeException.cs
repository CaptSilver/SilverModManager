namespace SilverModManager.Games.FromSoft.Merge;

/// <summary>
/// Two files could not be merged without losing content, so the merge was refused. The message gives the reason
/// without a path; the caller that knows which file it was working on adds that.
/// </summary>
/// <param name="message">The reason, in words a user can act on.</param>
/// <param name="missing">The builds whose vanilla regulation could not be found, when that is why; empty otherwise.</param>
public class MergeException(string message, IReadOnlyList<VanillaMissing>? missing = null) : Exception(message)
{
    /// <summary>The builds that have no vanilla regulation to compare their mods against; empty for every other refusal.</summary>
    public IReadOnlyList<VanillaMissing> Missing { get; } = missing ?? [];
}
