using NexusMods.Abstractions.Loadouts;

namespace NexusMods.Abstractions.Games;

/// <summary>
/// A sort order variety that can be re-ordered by LOOT's masterlist rules on request.
/// </summary>
public interface ILootSortSource
{
    /// <summary>
    /// Sorts the order and stores the result, or reports why it could not.
    /// </summary>
    Task<LootSortOutcome> SortAsync(SortOrderId sortOrderId, CancellationToken token = default);
}

/// <summary>
/// What a LOOT sort did.
/// </summary>
/// <param name="Succeeded">False when the sort found no valid order and left the stored one alone.</param>
/// <param name="CycleText">The cycle that blocked the sort, as LOOT words it; null when there was none.</param>
/// <param name="OverlapEdgesSkipped">True when record and asset overlap rules were not applied to the result.</param>
/// <param name="UnevaluatedConditions">How many masterlist conditions could not be evaluated and were skipped.</param>
/// <param name="MasterlistRevision">Identifies the masterlist revision the sort used.</param>
public record LootSortOutcome(
    bool Succeeded,
    string? CycleText,
    bool OverlapEdgesSkipped,
    int UnevaluatedConditions,
    string MasterlistRevision);
