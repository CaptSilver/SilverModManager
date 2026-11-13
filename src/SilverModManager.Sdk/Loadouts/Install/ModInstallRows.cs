using System.Reactive.Subjects;
using DynamicData;

namespace SilverModManager.Sdk.Loadouts.Install;

/// <summary>
/// The rows of one install. Its owner creates it before anything subscribes, then adds the rows once; a page that
/// attaches earlier sees an empty set and then the whole add burst.
/// </summary>
public sealed class ModInstallRows : IModInstallRows, IDisposable
{
    private readonly SourceCache<ModInstallRow, string> _rows = new(r => r.Key);
    private readonly BehaviorSubject<ModInstallTotals> _totals = new(new ModInstallTotals(0, 0, 0));

    /// <summary>A set that is never added to, for a page with nothing to show.</summary>
    public static readonly ModInstallRows Empty = new();

    public IObservable<IChangeSet<ModInstallRow, string>> Connect() => _rows.Connect();

    public IObservable<ModInstallTotals> Totals => _totals;

    public int Count => _rows.Count;

    /// <summary>Adds <paramref name="rows"/> as one change set.</summary>
    public void Add(IEnumerable<ModInstallRow> rows) => _rows.AddOrUpdate(rows);

    public bool TryGet(string key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out ModInstallRow row)
    {
        var found = _rows.Lookup(key);
        row = found.HasValue ? found.Value : null;
        return found.HasValue;
    }

    /// <summary>Publishes <paramref name="totals"/> unless they equal the last ones.</summary>
    public void SetTotals(ModInstallTotals totals)
    {
        if (_totals.Value != totals) _totals.OnNext(totals);
    }

    public void Dispose()
    {
        _rows.Dispose();
        _totals.Dispose();
    }
}
