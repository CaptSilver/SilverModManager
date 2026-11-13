using System.Reactive.Subjects;

namespace SilverModManager.Sdk.Loadouts.Install;

/// <summary>
/// One mod folder of an install. The job swaps in a new immutable <see cref="ModInstallRowState"/> on every step, and
/// per-row changes travel here rather than through the rows' change set, which would otherwise push one change per file.
/// </summary>
public sealed class ModInstallRow(string key, string name, int index, ModInstallRowState initial)
{
    private ModInstallRowState _current = initial;
    private readonly Subject<ModInstallRowState> _changed = new();

    public string Key { get; } = key;
    public string Name { get; } = name;

    /// <summary>The folder's place in the list, which is the order the rows show in.</summary>
    public int Index { get; } = index;

    public ModInstallRowState Current => Volatile.Read(ref _current);

    /// <summary>Raised on the reporting thread after the state is swapped; subscribers cross threads themselves.</summary>
    public IObservable<ModInstallRowState> Changed => _changed;

    /// <summary>The only mutator. Callers that set one row from several threads serialise themselves.</summary>
    public void Set(ModInstallRowState state)
    {
        Volatile.Write(ref _current, state);
        _changed.OnNext(state);
    }
}
