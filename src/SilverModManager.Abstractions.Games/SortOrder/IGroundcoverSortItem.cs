namespace SilverModManager.Abstractions.Games;

/// <summary>
/// A sort order row that can be marked as groundcover.
/// </summary>
public interface IGroundcoverSortItem
{
    /// <summary>Whether the row is loaded as groundcover.</summary>
    bool IsGroundcover { get; }
}
