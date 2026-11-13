using DynamicData;

namespace SilverModManager.Sdk.Loadouts.Install;

/// <summary>The mod folders of one install, for a page to list.</summary>
public interface IModInstallRows
{
    /// <summary>Every row as an add, replayed to a late subscriber. Rows are never removed.</summary>
    IObservable<IChangeSet<ModInstallRow, string>> Connect();

    /// <summary>The totals, replaying the latest to a late subscriber.</summary>
    IObservable<ModInstallTotals> Totals { get; }

    int Count { get; }
}
