using NexusMods.Paths;

namespace SilverModManager.Abstractions.Games;

/// <summary>
/// The file copies that bring the save the user plays today into a loadout's own save slot.
/// </summary>
/// <param name="Description">One line for the confirmation dialog, saying what will be copied and where.</param>
/// <param name="Copies">Source and destination pairs. Nothing is copied until the user confirms.</param>
public sealed record SaveAdoptionPlan(string Description, IReadOnlyList<SaveAdoptionPlan.Copy> Copies)
{
    /// <summary>
    /// One file to copy. The source is left in place.
    /// </summary>
    public readonly record struct Copy(AbsolutePath Source, AbsolutePath Destination);

    /// <inheritdoc />
    public bool Equals(SaveAdoptionPlan? other)
    {
        return other is not null && Description == other.Description && Copies.SequenceEqual(other.Copies);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Description);
        foreach (var copy in Copies) hash.Add(copy);
        return hash.ToHashCode();
    }
}
